using System;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Avalonia.Native
{
    /// <summary>
    /// Creates native macOS window implementations for use with Window(IWindowImpl).
    /// </summary>
    public static class AvaloniaNativeWindowFactory
    {
        /// <summary>
        /// Creates an NSPanel-backed window on the UI thread after the native backend is initialized.
        /// The caller transfers ownership to an Avalonia Window through its platform constructor.
        /// </summary>
        /// <param name="options">Creation-time panel options, or null for an ordinary panel.</param>
        /// <returns>The platform implementation owned by the constructed Window.</returns>
        public static IWindowImpl CreatePanel(AvaloniaNativePanelOptions? options = null)
        {
            Dispatcher.UIThread.VerifyAccess();
            if (AvaloniaLocator.Current.GetService<IWindowingPlatform>() is not AvaloniaNativePlatform platform)
            {
                throw new InvalidOperationException("The Avalonia.Native backend must be initialized before creating a panel.");
            }

            return platform.CreatePanel(options ?? new AvaloniaNativePanelOptions());
        }
    }

    /// <summary>
    /// Configures immutable native panel behavior before the window is initialized.
    /// </summary>
    public sealed class AvaloniaNativePanelOptions
    {
        /// <summary>
        /// Gets whether the panel can receive keyboard input without activating its owning application.
        /// The default is false. A non-activating panel becomes key only on an explicit input request.
        /// </summary>
        public bool IsNonActivating { get; init; }
    }
}
