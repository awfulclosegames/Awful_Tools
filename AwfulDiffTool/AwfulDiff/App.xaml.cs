using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;

using System.Runtime.InteropServices;

using AwfulDiff.Models;
using AwfulDiff.Services;
using AwfulDiff.ViewModels;

namespace AwfulDiff
{
    public partial class App : Application
    {
        //****************************************************************
        //  DEBUG TESTING
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;
        //  DEBUG TESTING
        //****************************************************************

        /// <summary>Set when launched from the CLI with three file args; drives the merge exit code.</summary>
        private MergeWindowViewModel? _cliMergeVm;

        protected override void OnStartup(StartupEventArgs e)
        {
            //****************************************************************
            //  DEBUG TESTING
            AttachConsole(ATTACH_PARENT_PROCESS);
            //  DEBUG TESTING
            //****************************************************************

            base.OnStartup(e);

            // Log WPF binding errors to console for debugging
#if DEBUG
            PresentationTraceSources.DataBindingSource.Listeners.Add(
                new ConsoleTraceListener()
                {
                    Filter = new EventTypeFilter(SourceLevels.Warning | SourceLevels.Error)
                });
#endif

            var args = CommandLineParser.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());

            if (args.ShowHelp)
            {
                Console.WriteLine(CommandLineParser.Usage);
                Shutdown(0);   // no window opens — explicit shutdown required under OnLastWindowClose
                return;
            }

            if (args.Files.Count >= 3)
            {
                ShowMergeWindow(args.Files[0], args.Files[1], args.Files[2], args.OutputPath);
            }
            else
            {
                var win = new MainWindow(
                    args.Files.Count > 0 ? args.Files[0] : null,
                    args.Files.Count > 1 ? args.Files[1] : null);
                // With StartupUri gone, Application.MainWindow must be set explicitly — StartMerge
                // closes Application.Current.MainWindow when it opens the merge window.
                MainWindow = win;
                win.Show();
            }
        }

        /// <summary>
        /// CLI 3-way merge launch: loads base/A/B and shows the merge window directly (no
        /// MainWindow, no setup dialog). A load failure exits with code 1 so the VCS tool
        /// falls back to its own merge.
        /// </summary>
        private void ShowMergeWindow(string basePath, string pathA, string pathB, string? outputPath)
        {
            var loaded = new (string Path, FileModel? Model)[]
            {
                (basePath, FileService.LoadFile(basePath)),
                (pathA,    FileService.LoadFile(pathA)),
                (pathB,    FileService.LoadFile(pathB))
            };

            var failed = loaded.Where(f => f.Model == null).Select(f => f.Path).ToList();
            if (failed.Count > 0)
            {
                MessageBox.Show("Could not load file(s):\n" + string.Join("\n", failed),
                                "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);   // no window opens — explicit shutdown required under OnLastWindowClose
                return;
            }

            _cliMergeVm = new MergeWindowViewModel(loaded[0].Model!, loaded[1].Model!, loaded[2].Model!, outputPath);
            var win = new ThreeWayMergeWindow(_cliMergeVm);
            MainWindow = win;   // same explicit-assignment requirement as the diff path
            win.Closed += (s, ev) => _cliMergeVm!.EnsureMergedOutputWritten();
            win.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Merge-tool contract (git trustExitCode, p4 fallback): exit 0 only when the merged
            // result reached disk. Diff mode and in-app merges always exit 0.
            if (_cliMergeVm != null && !_cliMergeVm.WroteMergedOutput)
                Environment.ExitCode = 1;

            base.OnExit(e);
        }
    }
}
