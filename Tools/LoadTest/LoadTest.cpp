// Load test: LoadTest.exe <devkit Binaries\Win64> <plugin dll>...
// Resolves all imports of the plugin DLLs against the devkit's ShooterGameEditor-*.dll files (Windows loader) and
// runs the modules' static initialization. The DLLs of a multi-module plugin are loaded in the given (link) order
// into one process, so a module's imports from the plugin modules before it bind to the already loaded DLLs.
// Exit code 0 on success.
#include <windows.h>
#include <cstdio>

int wmain(int argc, wchar_t** argv)
{
	if (argc < 3) { fwprintf(stderr, L"usage: LoadTest <devkit binaries dir> <dll>...\n"); return 2; }
	SetDllDirectoryW(argv[1]);
	for (int Arg = 2; Arg < argc; ++Arg)
	{
		HMODULE Module = LoadLibraryExW(argv[Arg], nullptr, 0);
		if (!Module)
		{
			DWORD Error = GetLastError();
			wchar_t Message[512] = {};
			FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, Error, 0, Message, 512, nullptr);
			wprintf(L"LOAD FAILED: %s: error %lu: %s\n", argv[Arg], Error, Message);
			return 1;
		}
		// UE 5.8 modules register themselves through a static FModuleInitializerEntry (no InitializeModule export),
		// so a successful load also means the registration with the devkit's Core module ran.
		wprintf(L"LOADED %s at %p\n", argv[Arg], (void*)Module);
	}
	return 0;
}
