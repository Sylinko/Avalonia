#!/usr/bin/env bash
set -euo pipefail

# This compatibility fork deliberately preserves the upstream package identity/version.
# Update this version and upstream base together when porting the patch series.
package_version=12.1.3
upstream_commit=8eeda4f6f546165b3f72e63c9f42247abb306905
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output="$repo_root/artifacts/sylinko-native"
deployment_target=10.13
while (($#)); do
    case "$1" in
        --output) output="$2"; shift 2 ;;
        --deployment-target) deployment_target="$2"; shift 2 ;;
        *) echo "Usage: $0 [--output directory] [--deployment-target version]" >&2; exit 2 ;;
    esac
done
[[ $(uname -s) == Darwin ]] || { echo 'A macOS host with Xcode is required.' >&2; exit 1; }
cd "$repo_root"
git merge-base --is-ancestor "$upstream_commit" HEAD || {
    echo 'This patch series must be based on upstream Avalonia 12.1.3.' >&2; exit 1;
}
mkdir -p "$output"
output=$(cd -- "$output" && pwd)

# Generate the native header once through the existing IDL project. Native compilation
# stays in Xcode so the deployment-target override is explicit and never edits the project.
dotnet msbuild native/Avalonia.Native/Avalonia.Native.macOS.proj -restore -t:GenerateMicroComItems \
    -p:Configuration=Release -p:Version="$package_version" -p:BuildAvaloniaNativeXcodeProject=false
xcodebuild -quiet -project native/Avalonia.Native/src/OSX/Avalonia.Native.OSX.xcodeproj \
    -configuration Release CONFIGURATION_BUILD_DIR="$repo_root/artifacts/native/Release" \
    MACOSX_DEPLOYMENT_TARGET="$deployment_target" ONLY_ACTIVE_ARCH=NO \
    GCC_WARN_INHIBIT_ALL_WARNINGS=YES

dotnet pack src/Avalonia.Native/Avalonia.Native.csproj -c Release --disable-build-servers \
    -p:Version="$package_version" -p:PackageVersion="$package_version" \
    -p:RepositoryUrl=https://github.com/Sylinko/Avalonia \
    -p:BuildAvaloniaNativeXcodeProject=false -p:UsedAvaloniaProducts= \
    --include-symbols -p:SymbolPackageFormat=snupkg -o "$output"

package="$output/Avalonia.Native.$package_version.nupkg"
python3 - "$package" "$deployment_target" <<'PYVERIFY'
import ctypes,hashlib,json,pathlib,re,subprocess,sys,tempfile,uuid,xml.etree.ElementTree as ET,zipfile
package=pathlib.Path(sys.argv[1])
with zipfile.ZipFile(package) as archive:
    metadata=ET.fromstring(archive.read('Avalonia.Native.nuspec')).find('{*}metadata')
    assert metadata is not None and metadata.findtext('{*}id')=='Avalonia.Native'
    assert metadata.findtext('{*}version')=='12.1.3'
    dependencies=metadata.findall('{*}dependencies/{*}group/{*}dependency')
    assert dependencies and {item.get('id') for item in dependencies}=={'Avalonia'},'Unexpected unpublished/transitive package dependency'
    for framework in ('net8.0','net10.0'):
        assert f'lib/{framework}/Avalonia.Native.dll' in archive.namelist()
    with tempfile.TemporaryDirectory() as temporary:
        binary=pathlib.Path(temporary)/'libAvaloniaNative.dylib'
        binary.write_bytes(archive.read('runtimes/osx/native/libAvaloniaNative.dylib'))
        architectures=subprocess.check_output(['lipo','-archs',str(binary)],text=True).split()
        assert set(architectures)=={'arm64','x86_64'},architectures
        print('Native architectures:',', '.join(architectures))
        # Exercise the original GUID materialization boundary, not a same-binary comparison.
        native=ctypes.CDLL(str(binary))
        idl=pathlib.Path('src/Avalonia.Native/avn.idl').read_text()
        interfaces=re.findall(r'\[uuid\(([^)]+)\)[^\]]*\]\s*interface\s+(\w+)',idl)
        for value,name in interfaces:
            actual=bytes((ctypes.c_ubyte*16).in_dll(native,'IID_'+name))
            assert actual==uuid.UUID(value).bytes_le, 'Native GUID differs from IDL: '+name
        print('Native interface GUIDs verified:',len(interfaces))
provenance={
    'package':'Avalonia.Native','version':'12.1.3','sourceRepository':'Sylinko/Avalonia',
    'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
    'isWorkingTreeDirty':bool(subprocess.check_output(['git','status','--porcelain','--untracked-files=all','--','native','src/Avalonia.Native','scripts/sylinko','docs/sylinko','.github/workflows/sylinko-native.yml'],text=True)),
    'upstreamCommit':'8eeda4f6f546165b3f72e63c9f42247abb306905',
    'macOSDeploymentTarget':sys.argv[2],
    'sha256':hashlib.sha256(package.read_bytes()).hexdigest()
}
package.with_suffix('.provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
print(package)
PYVERIFY
