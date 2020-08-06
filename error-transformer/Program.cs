﻿using Fclp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static Battousai.Utils.ConsoleUtils;

namespace error_transformer
{
    class Program
    {
        private const int BufferSize = 4096;
        private const int ProgressBarWidth = 16;
        private const int OneMinute = 60;
        private const int OneHour = 3600;

        private static readonly Regex InterceptorFilenameRegex = new Regex(@"^interceptor(?:\.(\d+)\.(\d+))?\.log$", RegexOptions.IgnoreCase);

        private static int progressIndex;
        private static int progressLength;
        private static readonly object progressLock = new object();
        private static int numProcessed = 0;
        private static int numErrors = 0;

        private static void IncrementNumProcessed() => Interlocked.Increment(ref numProcessed);
        private static void IncrementNumErrors() => Interlocked.Increment(ref numErrors);

        static void Main(string[] args)
        {
            RunLoggingExceptions(() =>
            {
                var duration = MeasureDuration(() =>
                {
                    // Setup command-line argument parsing
                    var parser = new FluentCommandLineParser<Args>();

                    parser.Setup(x => x.InputFolder)
                        .As('i', "input-folder");

                    parser.Setup(x => x.OutputFolder)
                        .As('o', "output-folder");

                    var results = parser.Parse(args);

                    if (results.HasErrors || !ValidateArgs(parser.Object))
                    {
                        Log("Invalid command-line parameters.");
                        Log(@"Example usage: .\error-transformer.exe -i \\wjv-gendfs01\telelogs\2020\06\26 -o c:\tm-error-logs\2020\06\26");
                        return;
                    }

                    var parameters = parser.Object;

                    if (!Directory.Exists(parameters.InputFolder))
                    {
                        Log($"The folder '{parameters.InputFolder}' does not exist.");
                        return;
                    }

                    // Get already-processed errors from output folder
                    List<string> alreadyProcessedFiles = new List<string>();

                    if (Directory.Exists(parameters.OutputFolder))
                    {
                        alreadyProcessedFiles = Directory.GetFiles(parameters.OutputFolder, "*.zip")
                            .Select(x => Path.GetFileName(x))
                            .ToList();
                    }

                    // Get list of files to process (ignoring already-processed ones)
                    var allFilesToProcess = Directory.GetFiles(parameters.InputFolder);

                    var filesToProcess = allFilesToProcess
                        .Where(x => !alreadyProcessedFiles.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    var ignoredCount = allFilesToProcess.Length - filesToProcess.Count;
                    var addendum = (ignoredCount == 0 ? "" : $" ({ignoredCount:#,##0} ignored)");

                    if (filesToProcess.Count == 0)
                    {
                        Log($"No files to process{addendum}.");
                        return;
                    }
                    else
                        Log($"Found {filesToProcess.Count:#,##0} {(filesToProcess.Count == 1 ? "file" : "files")} to process{addendum}.");

                    if (!Directory.Exists(parameters.OutputFolder))
                        Directory.CreateDirectory(parameters.OutputFolder);

                    // Process files
                    var saveCursorVisible = Console.CursorVisible;
                    Console.CursorVisible = false;
                    var stopwatch = new Stopwatch();

                    stopwatch.Start();

                    try
                    {
                        progressIndex = 1;
                        progressLength = filesToProcess.Count;

                        filesToProcess
                            .AsParallel()
                            .ForAll(x => ProcessFile(x, parameters.OutputFolder, stopwatch));

                        LogProgressCompleted();
                    }
                    finally
                    {
                        Console.CursorVisible = saveCursorVisible;
                    }
                });

                Log();
                Log($"Finished in {NormalizeDuration(duration)}.");
            }, false, false);
        }

        private static void ProcessFile(string filename, string outputFolder, Stopwatch stopwatch)
        {
            string newZipFilename = null;

            try
            {
                var justFilename = Path.GetFileName(filename);

                LogProgress(justFilename, stopwatch);

                using (var archive = ZipFile.OpenRead(filename))
                {
                    // Get list of inner files to collect
                    var interceptorFiles = GetInterceptorFiles(archive)
                        .ToList();

                    // Stream inner files into a new combined log file in new zip file
                    newZipFilename = Path.Combine(outputFolder, justFilename);

                    using (var newFile = ZipFile.Open(newZipFilename, ZipArchiveMode.Create))
                    {
                        var entry = newFile.CreateEntry("interceptor.log");

                        using (var destinationStream = entry.Open())
                        {
                            interceptorFiles
                                .ForEach(interceptorFile =>
                                {
                                    using (var sourceStream = interceptorFile.Open())
                                    {
                                        CopyStream(sourceStream, destinationStream);
                                    }
                                });

                            destinationStream.Flush();
                        }
                    }
                }

                IncrementNumProcessed();
            }
            catch (Exception ex)
            {
                // On error, delete new zip file (if it exists) 
                if (!String.IsNullOrWhiteSpace(newZipFilename) && File.Exists(newZipFilename))
                {
                    try
                    {
                        File.Delete(newZipFilename);
                    }
                    catch (Exception)
                    {
                        // Consume any exception
                    }
                }

                // ...and create *.error file with the error details
                var errorFilename = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(filename)}.error");

                using (var writer = new StreamWriter(errorFilename, false, Encoding.UTF8))
                {
                    writer.WriteLine($"<{ex.GetType().Name}> {ex.Message}");
                    writer.WriteLine(ex.StackTrace);
                }

                IncrementNumErrors();
            }
        }

        private static void CopyStream(Stream src, Stream dest)
        {
            var buffer = new byte[BufferSize];
            int len;
            while ((len = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dest.Write(buffer, 0, len);
            }
        }

        private static IEnumerable<ZipArchiveEntry> GetInterceptorFiles(ZipArchive archive)
        {
            // Group interceptor-named entries by date
            var groupedEntries = archive.Entries
                .Select(x =>
                {
                    var filename = Path.GetFileName(x.FullName);
                    var match = InterceptorFilenameRegex.Match(filename);

                    if (!match.Success)
                        return null;

                    var dateString = match.Groups[1].Value;
                    var isMainFile = String.IsNullOrWhiteSpace(dateString);

                    return new
                    {
                        Entry = x,
                        Filename = filename,
                        IsMainFile = isMainFile,
                        Date = isMainFile ? Int64.MaxValue : Int64.Parse(match.Groups[1].Value)
                    };
                })
                .Where(x => x != null)
                .GroupBy(x => new { x.Date, x.IsMainFile })
                .OrderByDescending(x => x.Key.Date);

            // Get the non-dated, main interceptor entry
            var mainInterceptorEntry = groupedEntries
                .TakeWhile(x => x.Key.IsMainFile)
                .FirstOrDefault();

            // Determine the max date of the dated entries
            var maxDate = groupedEntries
                .SkipWhile(x => x.Key.IsMainFile)
                .Select(x => (long?)x.Key.Date)
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (maxDate == null)
            {
                // No max date indicates no other interceptor log except the main one
                return mainInterceptorEntry
                    .EmptyIfNull()
                    .Select(x => x.Entry)
                    .ToList();
            }
            else
            {
                // Otherwise, order all of the max date entries (and main interceptor entry), and
                // return their ZipArchiveEntry values.
                var otherInterceptorEntries = groupedEntries
                    .SkipWhile(x => x.Key.IsMainFile)
                    .First(x => x.Key.Date == maxDate)
                    .OrderBy(x => x.Filename);

                return otherInterceptorEntries
                    .Concat(mainInterceptorEntry.EmptyIfNull())
                    .Select(x => x.Entry)
                    .ToList();
            }
        }

        private static string DisplayDuration(TimeSpan duration)
        {
            int seconds = (int)duration.TotalSeconds;

            if (seconds < OneMinute)
                return $"{(seconds < 1 ? 1 : seconds)} sec";
            else if (seconds < OneHour)
            {
                int minutes = seconds / OneMinute;
                int leftoverSeconds = seconds % OneMinute;

                if (leftoverSeconds == 0)
                    return $"{minutes} min";
                else
                    return $"{minutes} min, {leftoverSeconds} sec";
            }
            else
            {
                int hours = seconds / OneHour;
                int leftoverMinutes = (seconds % OneHour) / OneMinute;

                if (leftoverMinutes == 0)
                    return $"{hours} {(hours == 1 ? "hour" : "hours")}";
                else
                    return $"{hours} {(hours == 1 ? "hour" : "hours")}, {leftoverMinutes} min";
            }
        }

        private static void LogProgress(string message, Stopwatch stopwatch)
        {
            lock (progressLock)
            {
                var text = $"Processing ({progressIndex:#,##0} of {progressLength:#,##0}) — {message}";
                var percentProgress = Math.Min(Math.Max((progressIndex - 1.0) / progressLength, 0), 1);
                var bars = new String('=', (int)Math.Floor(ProgressBarWidth * percentProgress));
                var spaces = new String(' ', ProgressBarWidth - bars.Length);
                var currentDuration = stopwatch.Elapsed;
                string progressText;

                if (progressIndex < 3)
                {
                    progressText = $"{DisplayDuration(currentDuration)} elapsed ({(100 * percentProgress):0.0} %) [{bars}{spaces}]";
                }
                else
                {
                    var timeLeft = TimeSpan.FromSeconds(currentDuration.TotalSeconds * (progressLength - progressIndex) / progressIndex);

                    progressText = $"{DisplayDuration(currentDuration)} elapsed <{DisplayDuration(timeLeft)} left> ({(100 * percentProgress):0.0} %) [{bars}{spaces}]";
                }

                var extraSpace = Console.WindowWidth - text.Length - progressText.Length;

                if (extraSpace >= 0)
                {
                    text = text.PadRight(Console.WindowWidth - progressText.Length, ' ') + progressText;
                }
                else
                {
                    text = $"{text.Substring(0, Console.WindowWidth - progressText.Length - 3)}...{progressText}";
                }

                var startingPos = Console.CursorTop;
                Console.Write(text);
                Console.SetCursorPosition(0, startingPos);

                progressIndex += 1;
            }
        }

        private static void LogProgressCompleted()
        {
            var itemsText = $"{numProcessed:#,##0} {(numProcessed == 1 ? "file" : "files")}";
            var errorsText = numErrors == 0 ? "" : $" ({numErrors:#,##0} {(numErrors == 1 ? "error" : "errors")})";

            var startingPos = Console.CursorTop;
            Console.Write($"Processing {itemsText} completed{errorsText}.".PadRight(Console.WindowWidth, ' '));
            Console.SetCursorPosition(0, startingPos + 1);
        }

        private static bool ValidateArgs(Args args)
        {
            Func<string, bool> isNull = str => String.IsNullOrWhiteSpace(str);

            if (isNull(args.InputFolder) || isNull(args.OutputFolder))
                return false;

            return true;
        }
    }
}
