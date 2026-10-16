SPELL WORKSPACE - OPTIONAL BUILD REQUIREMENTS

To open the portable editor, run SpellWorkspace.exe. It already includes its
.NET desktop runtime. No installation is required just to open the editor.

To use Build All, close the editor and run Install Build Requirements.cmd
in the main app folder. Choose 1 to install missing tools. Existing suitable
tools are detected and skipped. You can also choose each installer separately.
Close and reopen the editor after setup, and restart Windows if requested.

Included in the App ZIP:
- .NET 10 SDK x64: full Microsoft installer, with server runtimes.
- Visual Studio 2022 Build Tools: official online bootstrapper, configured
  for the x64 C++ compiler and recommended Windows SDK components.
- Git for Windows x64: full installer, for the first world-data setup and downloading Imlight source.

C++ Build Tools needs internet and several GB of disk space. It is not a
complete offline installer. Server builds also download NuGet packages.

Spell Workspace's settings, compiler cache and temporary folders remain
beside its EXE. The third-party development tools are installed into Windows
and may use their own normal system installation/cache directories.
This menu does not turn the Microsoft or Git installations into portable tools.

The setup menu verifies each bundled installer's SHA256 and digital signature
before opening it. Installers are unmodified upstream downloads. Their own
license terms apply and are displayed by their setup programs.

The Source ZIP includes the menu and installer manifest, not the large EXEs.
Use the App ZIP with installers, or obtain the files from the official Source
URLs in installers.json and place them in this Installers folder.

Official pages:
https://dotnet.microsoft.com/en-us/download/dotnet/10.0
https://learn.microsoft.com/en-us/visualstudio/install/use-command-line-parameters-to-install-visual-studio?view=vs-2022
https://git-scm.com/install/windows
