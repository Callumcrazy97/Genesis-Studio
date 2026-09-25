using Genesis.Rendering.Primitives;
using Genesis.Application.Studio.Theme;
using Genesis.Application.Studio.Forms;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Studio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // --smoke-test is Build.bat driving the published executable with nobody watching, so no
        // dialog may wait for a click. Declared before anything can open a window.
        if (args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            Core.Diagnostics.UnattendedSession.Enable();
        }

        WinFormsApplication.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();
        StudioServices services = StudioServices.CreateDefault();
        services.Log.Information("Startup", "Build identity: " + StudioBuildInfo.ShortLabel);
        services.Log.Information("Startup", StudioBuildInfo.DiagnosticText);
        RenderingPreferencesBridge.Apply(services.Settings.Current);
        ThemeService.ApplySettings(services.Settings.Current);
        WinFormsApplication.SetDefaultFont(ThemeService.InterfaceFont);

        WinFormsApplication.ThreadException += (_, eventArgs) =>
        {
            services.Log.Error("Unhandled UI exception", eventArgs.Exception.Message, eventArgs.Exception);
            if (!Core.Diagnostics.UnattendedSession.IsActive) MessageBox.Show(
                eventArgs.Exception.Message,
                "Genesis Application encountered an error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            services.Log.Error("Background operation", "A background operation failed.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                services.Log.Error("Unhandled exception", exception.Message, exception);
                if (!Core.Diagnostics.UnattendedSession.IsActive)
                    MessageBox.Show("Genesis must close because a background operation failed.\n\n" + exception.Message +
                        "\n\nDetails were saved to the Studio log.", "Genesis — unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        if (args.Any(
                argument => string.Equals(
                    argument,
                    "--smoke-test",
                    StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string shaderToolchain = ShaderToolchainDeploymentProbe.ValidateLocalDeployment();
                using SplashForm splash = new();
                using ProjectHubForm projectHub = new(services);
                using PreferencesForm preferences = new(services.Settings);
                services.Log.Information("SmokeTest", $"Bundled shader toolchain passed: {shaderToolchain}");
                services.Log.Information("SmokeTest", "Published application smoke test passed.");
                return 0;
            }
            catch (Exception exception)
            {
                services.Log.Error("SmokeTest", "Published application smoke test failed.", exception);
                return 1;
            }
        }

        // Keep missing entries so the Project Hub can offer locate/remove recovery.
        try
        {
            using StudioApplicationContext context = new(services, args);
            WinFormsApplication.Run(context);
            return 0;
        }
        catch (Exception exception)
        {
            services.Log.Error("Startup", "Studio could not open.", exception);
            if (!Core.Diagnostics.UnattendedSession.IsActive)
                MessageBox.Show(exception.Message, "Genesis — startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
