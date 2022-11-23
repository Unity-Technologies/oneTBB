/*
    Copyright (c) 2022 Unity Technologies

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bee.BuildTools;
using Bee.Core;
using Bee.Core.Stevedore;
using Bee.NativeProgramSupport;
using Bee.Toolchain.Linux;
using Bee.Toolchain.LLVM;
using Bee.Toolchain.MacOS;
using Bee.Toolchain.VisualStudio;
using Bee.Toolchain.Windows;
using Bee.Tools;
using NiceIO;

class Build
{
    static void Main()
    {
        using var _ = new Bee.Core.BuildProgramContext();

        Backend.Current.StevedoreSettings = new StevedoreSettings
        {
            Manifest = { "manifest.stevedore" },
            EnforceManifest = true,
        };

        foreach (var sdk in Sdks.ForCurrentPlatformEditorSdks())
        {
            foreach (CodeGen codeGen in Enum.GetValues(typeof(CodeGen)))
            {
                var programConfiguration = new ProgramConfiguration(sdk, codeGen);
                SetupLib(programConfiguration);
                SetupStevedoreArtifact(programConfiguration);
            }
        }
    }

    enum CodeGen
    {
        Debug,
        Release
    }

    class ProgramConfiguration
    {
        public ProgramConfiguration(Sdk sdk, CodeGen codeGen)
        {
            Sdk = sdk;
            CodeGen = codeGen;
        }

        public string GetTargetName(string separator = "_")
        {
            var targetNameParts = new List<string> {
                Sdk.Platform.DisplayName.ToLower(),
                Sdk.Architecture.DisplayName
            };

            if (CodeGen == CodeGen.Debug)
            {
                targetNameParts.Add("dbg");
            }

            return string.Join(separator, targetNameParts);
        }

        public string GetArtifactName(string separator = "_")
        {
            var artifactNameParts = new List<string> {
                "tbb",
                GetTargetName(separator: separator)
            };

            return string.Join(separator, artifactNameParts);
        }

        public Sdk Sdk { get; private set; }
        public CodeGen CodeGen { get; private set; }
    }

    static void SetupLib(ProgramConfiguration programConfiguration)
    {
        var cfg = CfgFor(programConfiguration.CodeGen);
        var targetId = programConfiguration.GetTargetName();

        NPath[] inputs;
        var commandLineArguments = new List<string>
        {
            $"arch={ArchFor(programConfiguration.Sdk.Architecture)}",
            $"compiler={CompilerFor(programConfiguration.Sdk)}",
            $"cfg={cfg}",
            $"tbb_build_prefix={targetId}",
            // It's usually a bad idea to run things in parallel from the build
            // graph where actions are already run in parallel
            // We use bee as a wrapper for make and only build one target at a
            // time so our build graph is linear and running things in parallel
            // is not a problem
            $"-j {Environment.ProcessorCount}",
        };
        Dictionary<string, string> environmentVariables;

        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).ToNPaths().ToArray() ?? Array.Empty<NPath>();
    
        if (programConfiguration.Sdk is WindowsSdk)
        {
            WindowsSdk windowsSdk = (WindowsSdk)programConfiguration.Sdk;

            var binPaths = windowsSdk.EnvironmentVariables["PATH"].Split(";").ToNPaths();
            inputs = binPaths
                .Concat(windowsSdk.IncludePaths)
                .Concat(windowsSdk.LibraryPaths)
                .ToArray();

            var compileFlags = new[] {
                "/D_ITERATOR_DEBUG_LEVEL=0"
            };

            commandLineArguments.Add($"CXXFLAGS=\"{string.Join(" ", compileFlags)}\"");

            environmentVariables = new Dictionary<string, string> {
                {"PATH", binPaths
                    .Concat(path)
                    .Select(p => p.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native))
                    .SeparateWith(";")},
                {"INCLUDE", windowsSdk.IncludePaths
                    .Select(p => p.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native))
                    .SeparateWith(";")},
                {"LIB", windowsSdk.LibraryPaths
                    .Select(p => p.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native))
                    .SeparateWith(";")}
            };
        }
        else if (programConfiguration.Sdk is MacSdk)
        {
            MacSdk macSdk = (MacSdk)programConfiguration.Sdk;

            inputs = new[] {
                macSdk.BinPath,
                macSdk.SysRoot
            };

            var compileFlags = new[] {
                $"-mmacosx-version-min={ChooseMinMacOSVersion(macSdk.Architecture)}"
            };

            commandLineArguments.Add($"CXXFLAGS=\"{string.Join(" ", compileFlags)}\"");

            environmentVariables = new Dictionary<string, string> {
                {"PATH", (new[] {macSdk.BinPath})
                    .Concat(path)
                    .Select(p => p.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native))
                    .SeparateWith(":")},
                {"SDKROOT", macSdk.SysRoot.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native)}
            };
        }
        else if (programConfiguration.Sdk is LinuxClangSdk)
        {
            LinuxClangSdk linuxClangSdk = (LinuxClangSdk)programConfiguration.Sdk;

            inputs = new[] {
                linuxClangSdk.SysRoot,
                linuxClangSdk.GccToolchain,
                linuxClangSdk.ToolsPath
            };

            var compileFlags = new[] {
                $"--sysroot={linuxClangSdk.SysRoot.ResolveWithFileSystem().MakeAbsolute().InQuotes(SlashMode.Native)}",
                $"--gcc-toolchain={linuxClangSdk.GccToolchain.ResolveWithFileSystem().MakeAbsolute().InQuotes(SlashMode.Native)}",
                $"-target {linuxClangSdk.TargetTriple}",
                "-D_GLIBCXX_USE_CXX11_ABI=0"
            };

            var linkFlags = compileFlags.Concat(new[] {
                "-fuse-ld=lld",
                "-static-libstdc++"
            });

            commandLineArguments.Add($"CXXFLAGS='{string.Join(" ", compileFlags)}'");
            commandLineArguments.Add($"LDFLAGS='{string.Join(" ", linkFlags)}'");

            environmentVariables = new Dictionary<string, string> {
                {"PATH", (new[] {linuxClangSdk.ToolsPath})
                    .Concat(path)
                    .Select(p => p.ResolveWithFileSystem().MakeAbsolute().ToString(SlashMode.Native))
                    .SeparateWith(":")}
            };
        }
        else
        {
            throw new Exception($"Unsupported Sdk {programConfiguration.Sdk} for {nameof(SetupLib)}");
        }
        
        var buildDirectory = new NPath($"build/{targetId}_{cfg}");
        var installDirectory = new NPath($"builds/{targetId}");

        Backend.Current.AddAction(actionName: "make",
            targetFiles: new NPath[] {},
            inputs: inputs,
            executableStringFor: "make",
            commandLineArguments: commandLineArguments.ToArray(),
            environmentVariables: environmentVariables,
            targetDirectories: new[] {buildDirectory}
        );

        if (programConfiguration.Sdk is WindowsSdk)
        {
            Install_Windows(buildDirectory, installDirectory, targetId);
        }
        else if (programConfiguration.Sdk is LinuxClangSdk || programConfiguration.Sdk is MacSdk)
        {
            Install_LinuxOrMacOS(buildDirectory, installDirectory, targetId);
        }

        Backend.Current.AddAction(actionName: "cmake",
            targetFiles: new [] {
                installDirectory.Combine("cmake/TBBConfig.cmake"),
                installDirectory.Combine("cmake/TBBConfigVersion.cmake"),
            },
            inputs: new [] {buildDirectory, installDirectory.Combine("include/tbb/tbb_stddef.h")},
            executableStringFor: "cmake",
            commandLineArguments: new[] {
                $"-DINSTALL_DIR={installDirectory.Combine("cmake").InQuotes(SlashMode.Native)}",
                $"-DSYSTEM_NAME={SystemNameFor(programConfiguration.Sdk)}",
                $"-DTBB_VERSION_FILE={installDirectory.Combine("include/tbb/tbb_stddef.h").InQuotes(SlashMode.Native)}",
                "-DINC_REL_PATH=../include",
                "-DLIB_REL_PATH=../lib",
                "-DBIN_REL_PATH=../bin",
                "-P cmake/tbb_config_installer.cmake"
            },
            environmentVariables: environmentVariables,
            targetDirectories: new NPath[] {}
        );
    }

    static string ArchFor(Architecture architecture)
    {
        if (architecture is x64Architecture) return "intel64";
        if (architecture is Arm64Architecture) return "arm64";
        throw new Exception($"Unsupported architecture {architecture} for {nameof(ArchFor)}");
    }

    static string CompilerFor(Sdk sdk)
    {
        if (sdk is VisualStudioSdk) return "cl";
        if (sdk is ClangSdk || sdk is LinuxClangSdk) return "clang";
        throw new Exception($"Unsupported Sdk {sdk} for {nameof(CompilerFor)}");
    }

    static string CfgFor(CodeGen codeGen)
    {
        if (codeGen is CodeGen.Release) return "release";
        if (codeGen is CodeGen.Debug) return "debug";
        throw new Exception($"Unsupported CodeGen {codeGen} for {nameof(CfgFor)}");
    }

    static string ChooseMinMacOSVersion(Architecture architecture)
    {
        if (architecture is Arm64Architecture)
            return "11.0";

        return "10.14";
    }

    static string SystemNameFor(Sdk sdk)
    {
        if (sdk is WindowsSdk) return "Windows";
        if (sdk is MacSdk) return "Darwin";
        if (sdk is LinuxClangSdk) return "Linux";
        throw new Exception($"Unsupported Sdk {sdk} for {nameof(CompilerFor)}");
    }

    static void Install_Windows(NPath buildDirectory, NPath installDirectory, string targetId)
    {
        var binDirectory = installDirectory.Combine("bin");
        var libDirectory = installDirectory.Combine("lib");
        var includeDirectory = installDirectory.Combine("include");

        // Robocopy exit codes are unusual: 0-7 mean success. We need to use
        // a wrapper batch script to make 0 success and non-zero failure.
        Backend.Current.AddAction(actionName: "install",
            targetFiles: new NPath[] {},
            inputs: new[] {buildDirectory},
            executableStringFor: "robocopy.bat",
            commandLineArguments: new[] {
                $"{buildDirectory.ToString(SlashMode.Native)}",
                $"{binDirectory.ToString(SlashMode.Native)}",
                "/s",
                "tbb*.dll",
                "tbb*.pdb"
            },
            targetDirectories: new NPath[] {installDirectory.Combine("bin")}
        );
        
        Backend.Current.AddAction(actionName: "install",
            targetFiles: new NPath[] {},
            inputs: new[] {buildDirectory},
            executableStringFor: "robocopy.bat",
            commandLineArguments: new[] {
                $"{buildDirectory.ToString(SlashMode.Native)}",
                $"{libDirectory.ToString(SlashMode.Native)}",
                "/s",
                "tbb*.lib"
            },
            targetDirectories: new NPath[] {installDirectory.Combine("lib")}
        );

        Backend.Current.AddAction(actionName: "install",
            targetFiles: new NPath[] {},
            inputs: new[] {buildDirectory},
            executableStringFor: "robocopy.bat",
            commandLineArguments: new[] {
                "include",
                $"{includeDirectory.ToString(SlashMode.Native)}",
                "/s"
            },
            targetDirectories: new NPath[] {installDirectory.Combine("include")}
        );

        Backend.Current.AddAliasDependency($"lib::{targetId}", new[] {
            binDirectory,
            libDirectory,
            includeDirectory
        });
    }

    static void Install_LinuxOrMacOS(NPath buildDirectory, NPath installDirectory, string targetId)
    {
        var libDirectory = installDirectory.Combine("lib");
        var includeDirectory = installDirectory.Combine("include");

        Backend.Current.AddAction(actionName: "install",
            targetFiles: new NPath[] {},
            inputs: new[] {buildDirectory},
            executableStringFor: "find",
            commandLineArguments: new[] {
                buildDirectory.ToString(SlashMode.Native),
                "-name",
                "libtbb*.*",
                "-exec",
                "cp",
                "{}",
                libDirectory.ToString(SlashMode.Native),
                "\\;"
            },
            targetDirectories: new NPath[] {installDirectory.Combine("lib")}
        );

        Backend.Current.AddAction(actionName: "install",
            targetFiles: new NPath[] {},
            inputs: new[] {buildDirectory},
            executableStringFor: "cp",
            commandLineArguments: new[] {"-r", "include", installDirectory.ToString(SlashMode.Native)},
            targetDirectories: new NPath[] {includeDirectory}
        );

        Backend.Current.AddAliasDependency($"lib::{targetId}", new[] {
            libDirectory,
            includeDirectory
        });
    }

    static void SetupStevedoreArtifact(ProgramConfiguration programConfiguration)
    {
        var targetId = programConfiguration.GetTargetName();
        var artifactName = programConfiguration.GetArtifactName(separator: "-");
        var artifactPath = new NPath($"artifacts/for-stevedore/{artifactName}.7z");

        var contents = new ZipArchiveContents();
        contents.AddFileToArchive("LICENSE");
        if (programConfiguration.Sdk is WindowsSdk)
        {
            contents.AddFileToArchive($"builds/{targetId}/bin", "bin"); 
        }
        contents.AddFileToArchive($"builds/{targetId}/lib", "lib"); 
        contents.AddFileToArchive($"builds/{targetId}/include", "include");
        contents.AddFileToArchive($"builds/{targetId}/cmake/TBBConfig.cmake", "cmake/TBBConfig.cmake"); 
        contents.AddFileToArchive($"builds/{targetId}/cmake/TBBConfigVersion.cmake", "cmake/TBBConfigVersion.cmake"); 
        ZipTool.SetupPack(artifactPath, contents);

        Backend.Current.AddAliasDependency($"buildzip::{targetId}", artifactPath);
    }
}
