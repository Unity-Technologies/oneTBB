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

// This file is derived from Projects/Jam/Toolchains.jam.cs in trunk
using System;
using System.Collections.Generic;
using System.Linq;
using Bee.Core;
using Bee.NativeProgramSupport;
using Bee.Stevedore.Program;
using Bee.Toolchain.Linux;
using Bee.Toolchain.MacOS;
using Bee.Toolchain.VisualStudio;
using Bee.Toolchain.Windows;
using Bee.Tools;

static class ToolChains
{
    // Windows Editor minspec: Windows 10 Version 1909 - build 18363 (but 18363 has no API target so we have to target 18362)
    public static readonly TargetWindowsVersion MinSupportedOSVersion = TargetWindowsVersion.Windows10_18362;

    public static T LocateVisualStudioSdkToUse<T>(SdkLocator<T> locator, Version msvcToolsetVersion, Version win10SdkVersion) where T : VisualStudioSdk
    {
        var candidates = locator.All;

        if (win10SdkVersion != null)
        {
            // If a version has been provided, we want to only use SDKs that have that specific version. In theory
            // newer SDKs should be easy to adopt, but in practice we have seen too many failures with other versions
            // to make it worth relaxing this for now.
            candidates = candidates.Where(sdk => sdk.Win10SdkVersion == win10SdkVersion);
        }

        if (msvcToolsetVersion != null)
        {
            // We'd like to do the same thing for the MSVC version, BUT unfortunately due to badly-behaved
            // libraries (Enlighten), compilation with newer standard libraries doesn't work, and is not trivial
            // to fix. So for MSVC we will only use the local install if it is exactly the right version, and
            // will ignore any newer toolchains.
            candidates = candidates.Where(sdk => sdk.MsvcToolsetVersion.Major == msvcToolsetVersion.Major
                && sdk.MsvcToolsetVersion.Minor == msvcToolsetVersion.Minor
                // Comparing the 'Build' version is necessary because 14.28 had
                // breaking changes between sub-minor release numbers
                // but if we're not explicitly requesting a build we don't care.
                && (sdk.MsvcToolsetVersion.Build == msvcToolsetVersion.Build || msvcToolsetVersion.Build == -1 ));
        }

        // Prefer locally installed SDKs over downloadable ones
        candidates = candidates.OrderBy(sdk => sdk.IsDownloadable ? 1 : 0)
            .ThenByDescending(sdk => sdk.MsvcVersion)
            .ThenByDescending(sdk => sdk.Win10SdkVersion);

        var locatedSdk = candidates.DefaultIfEmpty(null).FirstOrDefault();
        if (locatedSdk == null)
        {
            var hashKey = new ValueTuple<SdkLocator<T>, Version, Version>(locator, win10SdkVersion, msvcToolsetVersion);
            locatedSdk = Backend.Current.ExecuteOnce(hashKey, () =>
            {
                var message = "No Visual Studio SDK ";
                if (msvcToolsetVersion != null)
                {
                    message += $"with MSVC toolset version {msvcToolsetVersion.Major}.{msvcToolsetVersion.Minor} ";
                    if (win10SdkVersion != null)
                        message += "and ";
                }

                if (win10SdkVersion != null)
                    message += $"Win10SDK version {win10SdkVersion} ";

                message += "could be found.\n";

                T resultSdk = locator.UserDefault;
                message += resultSdk != null
                    ? $"The UserDefault SDK with MSVC toolset version {resultSdk.MsvcToolsetVersion} and Win10 SDK version {resultSdk.Win10SdkVersion} will be used; be aware that this may cause build issues.\n"
                    : "No default VisualStudio SDK was found on this machine.\n";

                message += "\nFor reference, the following MSVC toolset versions are available:\n";
                message += locator.All.Exclude(locator.Dummy).Select(sdk => $"\t{sdk.MsvcToolsetVersion} ({(sdk.IsDownloadable ? "online" : "local")})").Distinct().SeparateWith("\n");
                message += "\n\nand the following Win10 SDK versions are available:\n";
                message += locator.All.Exclude(locator.Dummy).Select(sdk => $"\t{sdk.Win10SdkVersion} ({(sdk.IsDownloadable ? "online" : "local")})").Distinct().SeparateWith("\n");
                message += "\n";

                Errors.PrintWarning(message);
                return resultSdk;
            });
        }

        return locatedSdk ?? locator.Dummy;
    }

    public static Version VS2022VersionFromManifest => VSVersionFromManifest("vs2022-toolchain");

    public static string GetVersionFromManifest(string artifactName)
    {
        if (!Backend.Current.StevedoreSettings.Manifest.Entries.TryGetValue(new ArtifactName(artifactName), out var value))
            throw new KeyNotFoundException(
                $"Artifact \"{artifactName}\" doesn't appear to be described in any of the manifest files.");

        return value.ArtifactId.Version.VersionString;
    }

    public static Version VSVersionFromManifest(string stevedoreArtifactID)
    {
        // VS Stevedore artifacts are named using their toolchain version, but we want the VS version
        // for comparing them
        var vsVersionString = GetVersionFromManifest(stevedoreArtifactID);
        if (vsVersionString == null)
            return null;

        var parts = new System.Text.RegularExpressions.Regex(@"^(?<major>\d+)\.(?<minor>\d+)\.((?<build>\d{5})|([a-e0-9]{6}))$")
            .Match(vsVersionString);

        var version = parts.Groups.ContainsKey("build")?
            new Version(int.Parse(parts.Groups["major"].Value), int.Parse(parts.Groups["minor"].Value)):
            new Version(int.Parse(parts.Groups["major"].Value), int.Parse(parts.Groups["minor"].Value), int.Parse(parts.Groups["build"].Value));;

        return version;
    }

    public static Version Win10SdkVersionFromManifest
    {
        get
        {
            var versionString = GetVersionFromManifest("win10sdk");
            if (versionString == null)
                return null;

            return Version.Parse(versionString);
        }
    }

    public static ToolChain ForWindows(Architecture architecture, TargetWindowsVersion minOSVersion)
    {
        Version msvcVersion = VS2022VersionFromManifest;
        Version win10SdkVersion = Win10SdkVersionFromManifest;
        return new WindowsToolchain(LocateVisualStudioSdkToUse(WindowsSdk.LocatorFor(architecture), msvcVersion, win10SdkVersion), minOSVersion);
    }

    public static ToolChain ForLinux(Architecture architecture)
    {
        if (architecture is x64Architecture) return new LinuxClangToolchain(LinuxClangSdk.Locatorx64.UserDefaultOrLatest);
        throw new Exception($"Unsupported architecture {architecture} for {nameof(LinuxClangSdk)}");
    }

    public static ToolChain ForMacOS(Architecture architecture, string minOSVersion = null)
    {
        var locator = MacSdk.LocatorFor(architecture);
        var downloadableMacSdk = locator.DownloadableSdks.FirstOrDefault(sdk => sdk.Version == new Version(11, 1) && sdk.SupportedOnHostPlatform);
        return downloadableMacSdk != null ? new MacToolchain(downloadableMacSdk, minOSVersion) : new MacToolchain(locator.UserDefaultOrDummy, minOSVersion);
    }

    static Lazy<ToolChain[]> s_CurrentPlatformEditorToolChains = new Lazy<ToolChain[]>(() =>
    {
        if (HostPlatform.IsWindows)
            return new[] { ForWindows(Architecture.x64, MinSupportedOSVersion), ForWindows(Architecture.Arm64, MinSupportedOSVersion) };

        if (HostPlatform.IsLinux)
            return new[] { ForLinux(Architecture.x64) };

        if (HostPlatform.IsOSX)
            return new[] { ForMacOS(Architecture.x64, "10.14"), ForMacOS(Architecture.Arm64) };

        throw new ArgumentException($"Unsupported platform for {nameof(ForCurrentPlatformEditorTools)}");
    });

    public static ToolChain[] ForCurrentPlatformEditorTools() => s_CurrentPlatformEditorToolChains.Value;
}
