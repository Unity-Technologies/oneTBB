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
using Bee.Toolchain.VisualStudio.MsvcVersions;
using Bee.Toolchain.Windows;
using Bee.Tools;

static class Sdks
{
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
                && sdk.MsvcToolsetVersion.Build == msvcToolsetVersion.Build);
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

    public static string GetVersionFromManifest(string artifactName)
    {
        if (!Backend.Current.StevedoreSettings.Manifest.Entries.TryGetValue(new ArtifactName(artifactName), out var value))
            throw new KeyNotFoundException(
                $"Artifact \"{artifactName}\" doesn't appear to be described in any of the manifest files.");

        return value.ArtifactId.Version.VersionString;
    }

    public static Version VS2019VersionFromManifest
    {
        get
        {
            // VS2019 Stevedore artifacts are named using their toolchain version, but we want the VS version
            // for comparing them
            var vs2019VersionString = GetVersionFromManifest("vs2019-toolchain");
            if (vs2019VersionString == null)
                return null;

            var parts = new System.Text.RegularExpressions.Regex(@"^(?<major>\d+)\.(?<minor>\d+)\..+$")
                .Match(vs2019VersionString);

            var version = new Version(int.Parse(parts.Groups["major"].Value), int.Parse(parts.Groups["minor"].Value));
            if (version.Major == 14 && version.Minor == 28)
            {
                // MSVC 14.28 made multiple sub-minor releases that were incompatible, so we require a specific Build
                // version for this toolset. The Stevedore artifact name does not contain the Build number so we have
                // to just supply it here.
                version = new Version(version.Major, version.Minor, 29333);
            }

            return version;
        }
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

    public static WindowsSdk ForWindows(Architecture architecture)
    {
        Version msvcVersion = VS2019VersionFromManifest;
        Version win10SdkVersion = Win10SdkVersionFromManifest;
        return LocateVisualStudioSdkToUse(WindowsSdk.LocatorFor(Architecture.x64), msvcVersion, win10SdkVersion);
    }

    public static LinuxClangSdk ForLinux(Architecture architecture)
    {
        if (architecture is x64Architecture) return LinuxClangSdk.Locatorx64.UserDefaultOrLatest;
        throw new Exception($"Unsupported architecture {architecture} for {nameof(LinuxClangSdk)}");
    }

    public static MacSdk ForMacOS(Architecture architecture)
    {
        var locator = MacSdk.LocatorFor(architecture);
        return locator.DownloadableSdks.FirstOrDefault(sdk => sdk.Version == new Version(11, 1) && sdk.SupportedOnHostPlatform);
    }

    static Lazy<Sdk[]> s_CurrentPlatformEditorSdks = new Lazy<Sdk[]>(() =>
    {
        if (HostPlatform.IsWindows)
            return new[] { ForWindows(Architecture.x64) };

        if (HostPlatform.IsLinux)
            return new[] { ForLinux(Architecture.x64) };

        if (HostPlatform.IsOSX)
            return new[] { ForMacOS(Architecture.x64), ForMacOS(Architecture.Arm64) };

        throw new ArgumentException($"Unsupported platform for {nameof(ForCurrentPlatformEditorSdks)}");
    });

    public static Sdk[] ForCurrentPlatformEditorSdks() => s_CurrentPlatformEditorSdks.Value;
}
