using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("56e3434f-95de-49fe-bb59-2034ea457afb")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("Manual Focuser")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Find focus point with interactive manual focus steps")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("cwseo")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("manual focuser")]
[assembly: AssemblyCopyright("Copyright © 2026 cwseo")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.2017")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/squallseo/Nina.Manualfocuser")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://cafe.naver.com/skyguide")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Focuser, Manual, Step, Direct input, Interactive")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/squallseo/Nina.Manualfocuser/blob/master/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://github.com/squallseo/Nina.Manualfocuser/blob/master/Images/logo.png?raw=true")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://github.com/squallseo/Nina.Manualfocuser/blob/master/Images/screenshot.png?raw=true")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "https://github.com/squallseo/Nina.Manualfocuser/blob/master/Images/screenshot_alt.png?raw=true")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"NINA’s built-in autofocus is already fast and good enough, but it has a drawback:

the user has to judge and manually enter an appropriate autofocus step size.

Also, since it searches only based on star HFR, when trying to find a truly better focus point,

the user ends up fine-tuning by checking additional cues such as star shapes near the edges, spikes, and other details.

I want to create an environment that makes this process more convenient, 

and further, to build the foundation for eventually automating it.")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]