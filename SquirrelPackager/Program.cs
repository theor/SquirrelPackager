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
        public string GitRepo { get; set; }
        public string GitUsername { get; set; }
        public string GitPassword { get; set; }
    }
    class Program
    {
        static int Main(string[] args)
        {
            var p = new FluentCommandLineParser<Options>();

            p.Setup(o => o.Version).As('v', "version");
            p.Setup(o => o.NuSpec).As('t', "nuspec").Required();
            p.Setup(o => o.NugetExe).As('n',"nuget");
            p.Setup(o => o.SquirrelCom).As('s', "squirrel");
            p.Setup(o => o.ReleaseDir).As('r', "releasedir");
            p.Setup(o => o.LocalRepo).As("localrepo");
            p.Setup(o => o.GitRepo).As("repo");
            p.Setup(o => o.GitUsername).As("gitusername");
            p.Setup(o => o.GitPassword).As("gitpassword");
            p.SetupHelp("?", "help").Callback(text => Console.WriteLine(text));

            ICommandLineParserResult res = p.Parse(args);
            if (res.HasErrors)
            {
                Console.WriteLine(res.ErrorText);
                p.HelpOption.ShowHelp(p.Options);
                return 1;
            }
            if (!File.Exists(p.Object.NuSpec))
            {
                Console.WriteLine("{0} does not exist", p.Object.NuSpec);
                return 1;
            }
            if (p.Object.NugetExe == null)
                p.Object.NugetExe = FindFile(p.Object.NuSpec, ".nuget\\NuGet.exe");
            if (p.Object.SquirrelCom == null)
                p.Object.SquirrelCom = FindFile(p.Object.NuSpec, "squirrel.com");
            if (p.Object.ReleaseDir == null)
                p.Object.ReleaseDir = Path.GetFullPath(".");
            if (!File.Exists(p.Object.NugetExe))
            {
                Console.WriteLine("NuGet.exe not found");
                return 1;
            }

            Options options = p.Object;
            Manifest m;
            using(var fileStream = File.OpenRead(options.NuSpec))
                m = Manifest.ReadFrom(fileStream, false);
            var newVersion = SetPackageVersion(options, m);
            var nupkg = CreatePackage(options, m);
            var sqpkg = CreateSquirrelRelease(options, nupkg);
            var pushres = PushReleasesToGithub(options, newVersion);
            return 0;
        }

        private static object PushReleasesToGithub(Options options, Version newVersion)
        {
            using (var repo = new Repository(options.LocalRepo))
            {
                var st = repo.RetrieveStatus();
                var toCommit = st.Untracked.Concat(st.Modified).Select(x => x.FilePath).ToList();

                if (toCommit.Any())
                {
                    repo.Stage(toCommit);
                    var st2 = repo.RetrieveStatus();
                    var commit = repo.Commit("Release v." + newVersion);
                }
                var pushed = RunProcess("git", "push origin master", options.LocalRepo);
                //var origin = repo.Network.Remotes["origin"];
                //repo.Network.Fetch(origin);
                //var branch = repo.Branches["master"];
                //var pushOptions = new PushOptions
                //{
                //    CredentialsProvider =
                //        (url, fromUrl, types) =>
                //            new UsernamePasswordCredentials
                //            {
                //                Username = options.GitUsername,
                //                Password = options.GitPassword
                //            }
                //};
                //repo.Network.Push(origin, "refs/heads/master", pushOptions);
                //repo.Network.Push(branch, pushOptions);
            }
            return null;
        }

        private static Assembly FindAssembly(Options options, Manifest manifest)
        {
            var id = manifest.Metadata.Id;
            var asmf = manifest.Files.FirstOrDefault(f => Path.GetFileName(f.Source) == id + ".exe");
            if (asmf == null)
                return null;
            string fullPath = Path.Combine(Path.GetDirectoryName(options.NuSpec), asmf.Source);
            return Assembly.ReflectionOnlyLoadFrom(fullPath);
        }

        private static string CreateSquirrelRelease(Options options, string nupkg)
        {
            var p = RunProcess(options.SquirrelCom, string.Format("--releasify \"{0}\" --releaseDir=\"{1}\"", nupkg, options.ReleaseDir));
            return null;
        }
        private static string CreatePackage(Options options, Manifest m)
        {
            var expectedPackage = string.Format("{0}.{1}.nupkg", m.Metadata.Id, m.Metadata.Version);

            TryDelete(expectedPackage);

            var p = RunProcess(options.NugetExe, string.Format("pack \"{0}\"", options.NuSpec));
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

                var asm = FindAssembly(options, m);
                v2 = asm.GetName().Version;
            }

            m.Metadata.Version = v2.ToString();
            using (var fileStream = File.Open(options.NuSpec, FileMode.Truncate))
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
            var r = RunProcess(filename, args, out exitCode, output, errors, workingDir);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var line in output)
            {
                Console.WriteLine(line);
            }
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var line in errors)
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
            var proc = Process.Start(psi);
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
