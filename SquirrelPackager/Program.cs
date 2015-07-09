using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Fclp;
using LibGit2Sharp;
using Newtonsoft.Json;
using NuGet;
using FileMode = System.IO.FileMode;
using Version = System.Version;

namespace SquirrelPackager
{
    public class Options
    {
        public string NuSpec { get; set; }
        public string NugetExe { get; set; }
        public string SquirrelCom { get; set; }
        public string Version { get; set; }
        public string ReleaseDir { get; set; }
        public string LocalRepo { get; set; }

        public string SquirrelPackage { get; set; }
    }
    class Program
    {
        static int Main(string[] args)
        {
            FluentCommandLineParser<Options> p = new FluentCommandLineParser<Options>();

            p.Setup(o => o.Version).As('v', "version");
            p.Setup(o => o.NuSpec).As('t', "nuspec");
            p.Setup(o => o.NugetExe).As('n',"nuget");
            p.Setup(o => o.SquirrelCom).As('s', "squirrel");
            p.Setup(o => o.ReleaseDir).As('r', "releasedir");
            p.Setup(o => o.LocalRepo).As("localrepo");

            p.Setup(o => o.SquirrelPackage).As('p',"package");

            p.SetupHelp("?", "help").Callback(text => Console.WriteLine(text));

            ICommandLineParserResult res = p.Parse(args);
            if (res.HasErrors)
            {
                Console.WriteLine(res.ErrorText);
                p.HelpOption.ShowHelp(p.Options);
                return 1;
            }

            Options options = p.Object;
            if (options.SquirrelPackage != null)
            {
                if (!File.Exists(options.SquirrelPackage))
                {
                    Console.WriteLine("Squirrel Package file '{0}' does not exist");
                    return 1;
                }
                JsonConvert.PopulateObject(File.ReadAllText(options.SquirrelPackage), options);
                Environment.CurrentDirectory = Path.GetDirectoryName(options.SquirrelPackage);
            }

            //File.WriteAllText("squirrel.json", JsonConvert.SerializeObject(p.Object, Formatting.Indented));
            if (!File.Exists(options.NuSpec))
            {
                Console.WriteLine("{0} does not exist", options.NuSpec);
                return 1;
            }
            if (options.NugetExe == null)
                options.NugetExe = FindFile(options.NuSpec, ".nuget\\NuGet.exe");
            if (options.SquirrelCom == null)
                options.SquirrelCom = FindFile(options.NuSpec, "squirrel.com");
            if (options.ReleaseDir == null)
                options.ReleaseDir = Path.GetFullPath(".");
            if (!File.Exists(options.NugetExe))
            {
                Console.WriteLine("NuGet.exe not found");
                return 1;
            }

            Manifest m;
            using(FileStream fileStream = File.OpenRead(options.NuSpec))
                m = Manifest.ReadFrom(fileStream, false);
            Version newVersion = SetPackageVersion(options, m);

            WriteJekyllRelease(options, newVersion);
            string nupkg = CreatePackage(options, m);
            string sqpkg = CreateSquirrelRelease(options, nupkg);
            object pushres = PushReleasesToGithub(options, newVersion);
            return 0;
        }

        private const string MarkdownTemplate = @"---
layout: post
title: v{0}
date: {1}
categories: release
---
";

        private static void WriteJekyllRelease(Options options, Version newVersion)
        {
            var dateTime = DateTime.Now.ToString("yyyy-MM-dd");
            string text = String.Format(MarkdownTemplate, newVersion.ToString(3), dateTime);
            var format = string.Format("{0}-v{1}.markdown", dateTime, newVersion.ToString(3));
            File.WriteAllText(Path.Combine(options.LocalRepo, "_posts", format), text);
        }
        private static object PushReleasesToGithub(Options options, Version newVersion)
        {
            using (Repository repo = new Repository(options.LocalRepo))
            {
                RepositoryStatus st = repo.RetrieveStatus();
                List<string> toCommit = st.Untracked.Concat(st.Modified).Select(x => x.FilePath).Where(f => f.Contains("Releases")).ToList();

                if (toCommit.Any())
                {
                    repo.Stage(toCommit);
                    RepositoryStatus st2 = repo.RetrieveStatus();
                    Commit commit = repo.Commit("Release v." + newVersion);
                }
            }
            bool pushed = RunProcess("git", "push origin master", options.LocalRepo);
            return null;
        }

        private static Assembly FindAssembly(Options options, Manifest manifest)
        {
            string id = manifest.Metadata.Id;
            ManifestFile asmf = manifest.Files.FirstOrDefault(f => Path.GetFileName(f.Source) == id + ".exe");
            if (asmf == null)
                return null;
            string fullPath = Path.Combine(Path.GetDirectoryName(options.NuSpec), asmf.Source);
            return Assembly.ReflectionOnlyLoadFrom(fullPath);
        }

        private static string CreateSquirrelRelease(Options options, string nupkg)
        {
            bool p = RunProcess(options.SquirrelCom, string.Format("--releasify \"{0}\" --releaseDir=\"{1}\"", nupkg, options.ReleaseDir));
            return null;
        }
        private static string CreatePackage(Options options, Manifest m)
        {
            string expectedPackage = string.Format("{0}.{1}.nupkg", m.Metadata.Id, m.Metadata.Version);

            TryDelete(expectedPackage);

            bool p = RunProcess(options.NugetExe, string.Format("pack \"{0}\"", options.NuSpec));
            return p && File.Exists(expectedPackage) ? expectedPackage : null;
            //.nuget\NuGet.exe pack NewOrder\NewOrder.nuspec
        }

        private static Version SetPackageVersion(Options options, Manifest m)
        {
            Version v2;
            if (options.Version != null)
            {
                if (options.Version.StartsWith("+"))
                {
                    Version v = Version.Parse(m.Metadata.Version);
                    Version vinc = Version.Parse(options.Version.Substring(1));
                    v2 = new Version(v.Major + vinc.Major, v.Minor + vinc.Minor, v.Build + vinc.Build, 0);
                }
                else
                {
                    v2 = Version.Parse(options.Version);
                }
            }
            else
            {

                Assembly asm = FindAssembly(options, m);
                v2 = asm.GetName().Version;
            }

            m.Metadata.Version = v2.ToString();
            using (FileStream fileStream = File.Open(options.NuSpec, FileMode.Truncate))
                m.Save(fileStream, true);
            return v2;
        }

        private static void TryDelete(string expectedPackage)
        {
            if (File.Exists(expectedPackage))
                try
                {
                    File.Delete(expectedPackage);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
        }

        static string FindFile(string startDir, string relPath)
        {
            string dir = Path.GetDirectoryName(startDir) ?? "";
            string p = Path.Combine(dir, relPath);
            while (p != null && !File.Exists(p))
            {
                dir = Path.GetDirectoryName(dir);
                p = dir == null ? null : Path.Combine(dir, relPath);
            }
            return p;
        }

        public static bool RunProcess(string filename, string args, string workingDir = null)
        {
            int exitCode;
            List<string> output = new List<string>();
            List<string> errors = new List<string>();
            bool r = RunProcess(filename, args, out exitCode, output, errors, workingDir);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (string line in output)
            {
                Console.WriteLine(line);
            }
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (string line in errors)
            {
                Console.WriteLine(line);
            }
            Console.ResetColor();
            return r;
        }

        public static bool RunProcess(string filename, string args, out int exitCode, List<string> output = null, List<string> errors = null, string workingDir = null)
        {
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = filename,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? "",
            };
            Process proc = Process.Start(psi);
            Debug.Assert(proc != null, "proc != null");
            if (output != null)
            {
                while (!proc.StandardOutput.EndOfStream)
                {
                    string line = proc.StandardOutput.ReadLine();
                    output.Add(line);
                    // do something with line
                }
            }

            if (errors != null)
            {
                while (!proc.StandardError.EndOfStream)
                {
                    string line = proc.StandardError.ReadLine();
                    errors.Add(line);
                    // do something with line
                }
            }
            proc.WaitForExit();
            exitCode = proc.ExitCode;
            return proc.ExitCode == 0;
        }
    }
}
