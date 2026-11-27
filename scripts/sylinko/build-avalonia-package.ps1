[CmdletBinding()]
param(
    [string]$PackageVersion = '12.2.0',
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$upstreamCommit = '5d9e6d1b0e87337f74f233bead580db186412357'
$expectedAssemblyVersion = '12.2.0.0'
$expectedPublicKeyToken = 'c8d484a7012f9a8b'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputDir = Join-Path $repoRoot 'artifacts\sylinko-avalonia'

$env:DOTNET_ROLL_FORWARD = 'Major'

Push-Location $repoRoot
try {
    & git merge-base --is-ancestor $upstreamCommit HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "This patch series must be based on upstream Avalonia 12.2.0 ($upstreamCommit)."
    }

    if (-not $SkipTests) {
        & dotnet build "$repoRoot\tests\Avalonia.CollectibleTests\Avalonia.CollectibleTests.csproj" -c Release -f net10.0
        if ($LASTEXITCODE -ne 0) {
            throw 'Building Avalonia.CollectibleTests failed.'
        }

        & dotnet run --project "$repoRoot\tests\Avalonia.CollectibleTests\Avalonia.CollectibleTests.csproj" -c Release -f net10.0 --no-build --no-restore -- --no-progress
        if ($LASTEXITCODE -ne 0) {
            throw 'Avalonia.CollectibleTests failed.'
        }
    }

    & dotnet run --project "$repoRoot\nukebuild\_build.csproj" -- PackSylinkoAvalonia --force-nuget-version $PackageVersion --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'PackSylinkoAvalonia failed.'
    }

    $packagePath = Join-Path $outputDir "Avalonia.$PackageVersion.nupkg"
    $symbolsPath = Join-Path $outputDir "Avalonia.$PackageVersion.snupkg"

    if (-not (Test-Path $packagePath)) {
        throw "Expected NuGet package was not produced: $packagePath"
    }

    if (-not (Test-Path $symbolsPath)) {
        throw "Expected symbol package was not produced: $symbolsPath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Reflection.Metadata

    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            [void]$entries.Add($entry.FullName)
        }

        $nuspecEntry = $archive.GetEntry('Avalonia.nuspec')
        if ($null -eq $nuspecEntry) {
            throw 'Avalonia.nuspec missing from package.'
        }

        $nuspecXml = [System.Xml.Linq.XDocument]::Load($nuspecEntry.Open())
        $ns = $nuspecXml.Root.Name.Namespace
        $metadata = $nuspecXml.Root.Element($ns + 'metadata')
        $actualId = $metadata.Element($ns + 'id').Value
        $actualVersion = $metadata.Element($ns + 'version').Value
        if ($actualId -ne 'Avalonia') {
            throw "Unexpected package id: $actualId"
        }
        if ($actualVersion -ne $PackageVersion) {
            throw "Unexpected package version: $actualVersion (expected $PackageVersion)"
        }

        $depIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $depVersions = @{}
        foreach ($dep in $metadata.Descendants($ns + 'dependency')) {
            $depId = $dep.Attribute('id').Value
            [void]$depIds.Add($depId)
            $depVersions[$depId] = $dep.Attribute('version').Value
        }
        $expectedDeps = @('Avalonia.BuildServices', 'Avalonia.Remote.Protocol', 'MicroCom.Runtime')
        foreach ($expectedDep in $expectedDeps) {
            if (-not $depIds.Contains($expectedDep)) {
                throw "Expected dependency missing from Avalonia.nuspec: $expectedDep"
            }
        }
        if ($depVersions['Avalonia.Remote.Protocol'] -ne '12.2.0') {
            throw "Unexpected Avalonia.Remote.Protocol dependency version '$($depVersions['Avalonia.Remote.Protocol'])' (expected 12.2.0)"
        }
        if ($depIds.Count -ne $expectedDeps.Count) {
            throw "Unexpected extra dependencies in Avalonia.nuspec: $($depIds -join ', ')"
        }

        $requiredAssemblies = @(
            'Avalonia.Base.dll',
            'Avalonia.Controls.dll',
            'Avalonia.DesignerSupport.dll',
            'Avalonia.Dialogs.dll',
            'Avalonia.dll',
            'Avalonia.Markup.dll',
            'Avalonia.Markup.Xaml.dll',
            'Avalonia.Metal.dll',
            'Avalonia.MicroCom.dll',
            'Avalonia.OpenGL.dll',
            'Avalonia.Vulkan.dll'
        )

        foreach ($tfm in @('net8.0', 'net10.0')) {
            foreach ($dll in $requiredAssemblies) {
                foreach ($prefix in @('lib', 'ref')) {
                    $path = "$prefix/$tfm/$dll"
                    if (-not $entries.Contains($path)) {
                        throw "Missing required assembly entry: $path"
                    }
                }
            }

            $designerPath = "tools/$tfm/designer/Avalonia.Designer.HostApp.dll"
            if (-not $entries.Contains($designerPath)) {
                throw "Missing designer host tool: $designerPath"
            }
        }

        foreach ($requiredPath in @(
            'tools/netstandard2.0/Avalonia.Build.Tasks.dll',
            'analyzers/dotnet/cs/Avalonia.Generators.dll',
            'analyzers/dotnet/cs/Avalonia.Analyzers.CSharp.dll',
            'analyzers/dotnet/cs/Avalonia.Analyzers.VisualBasic.dll',
            'analyzers/dotnet/cs/Avalonia.Analyzers.CodeFixes.CSharp.dll',
            'build/Avalonia.props',
            'build/Avalonia.targets',
            'build/AvaloniaVersion.props',
            'buildTransitive/Avalonia.props',
            'buildTransitive/Avalonia.targets'
        )) {
            if (-not $entries.Contains($requiredPath)) {
                throw "Missing required package asset: $requiredPath"
            }
        }

        foreach ($basePath in @(
            'lib/net8.0/Avalonia.Base.dll',
            'ref/net8.0/Avalonia.Base.dll',
            'lib/net10.0/Avalonia.Base.dll',
            'ref/net10.0/Avalonia.Base.dll'
        )) {
            $baseEntry = $archive.GetEntry($basePath)
            $ms = [System.IO.MemoryStream]::new()
            try {
                $stream = $baseEntry.Open()
                try {
                    $stream.CopyTo($ms)
                }
                finally {
                    $stream.Dispose()
                }
                $ms.Position = 0

                $peReader = [System.Reflection.PortableExecutable.PEReader]::new($ms)
                try {
                    $mdReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
                    $asmDef = $mdReader.GetAssemblyDefinition()
                    $asmVersion = $asmDef.Version.ToString()
                    if ($asmVersion -ne $expectedAssemblyVersion) {
                        throw "Unexpected AssemblyVersion '$asmVersion' in $basePath (expected $expectedAssemblyVersion)"
                    }

                    $pubKeyBytes = $mdReader.GetBlobBytes($asmDef.PublicKey)
                    $sha1 = [System.Security.Cryptography.SHA1]::Create()
                    try {
                        $hash = $sha1.ComputeHash($pubKeyBytes)
                    }
                    finally {
                        $sha1.Dispose()
                    }
                    $tokenBytes = $hash[($hash.Length - 8)..($hash.Length - 1)]
                    [System.Array]::Reverse($tokenBytes)
                    $actualToken = ([System.BitConverter]::ToString($tokenBytes)).Replace('-', '').ToLowerInvariant()
                    if ($actualToken -ne $expectedPublicKeyToken) {
                        throw "Unexpected PublicKeyToken '$actualToken' in $basePath (expected $expectedPublicKeyToken)"
                    }

                    $foundCleanupType = $false
                    foreach ($typeHandle in $mdReader.TypeDefinitions) {
                        $typeDef = $mdReader.GetTypeDefinition($typeHandle)
                        $typeName = $mdReader.GetString($typeDef.Name)
                        $typeNs = $mdReader.GetString($typeDef.Namespace)
                        if ($typeNs -eq 'Avalonia' -and $typeName -eq 'CollectibleAssemblyCleanup') {
                            $isPublic = ($typeDef.Attributes -band [System.Reflection.TypeAttributes]::VisibilityMask) -eq [System.Reflection.TypeAttributes]::Public
                            if ($isPublic) {
                                $foundCleanupType = $true
                                break
                            }
                        }
                    }

                    if (-not $foundCleanupType) {
                        throw "Public type Avalonia.CollectibleAssemblyCleanup not found in $basePath"
                    }
                }
                finally {
                    $peReader.Dispose()
                }
            }
            finally {
                $ms.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $sourceCommit = (& git rev-parse HEAD).Trim()
    $dirtyPaths = & git status --porcelain --untracked-files=all -- src/Avalonia.Base src/Avalonia.Controls tests/Avalonia.CollectibleTests nukebuild/Build.cs scripts/sylinko docs/sylinko .github/workflows/sylinko-avalonia.yml
    $isDirty = [bool]($dirtyPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $sha256 = (Get-FileHash -Path $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $provenance = [ordered]@{
        package = 'Avalonia'
        version = $PackageVersion
        assemblyVersion = $expectedAssemblyVersion
        sourceRepository = 'Sylinko/Avalonia'
        sourceCommit = $sourceCommit
        isWorkingTreeDirty = $isDirty
        upstreamCommit = $upstreamCommit
        sha256 = $sha256
    }

    $provenancePath = Join-Path $outputDir "Avalonia.$PackageVersion.provenance.json"
    ($provenance | ConvertTo-Json -Depth 4) + "`n" | Set-Content -Path $provenancePath -Encoding utf8NoBOM

    Write-Host "Verified package: $packagePath"
    Write-Host "Provenance written: $provenancePath (dirty=$isDirty)"
}
finally {
    Pop-Location
}
