Plugins for compiling should be placed in this folder, in the predetermined layout (the same way they ship in UE)
You can obtain the sources by installing the plugin into a vanilla UE 5.8, and then copying it here.

For example:
GitSourceControl
GitSourceControl\GitSourceControl.uplugin
GitSourceControl\Source -- contains all source files

Folders of engine modules the devkit lacks (Config\EngineModulePlugins.txt, e.g. JsonUtilitiesEditor) are created
automatically from the vanilla engine by Tools\New-EngineModulePlugins.ps1 (Build.ps1 runs it): do not delete them.
