// SPDX-License-Identifier: MIT
#define COBJMACROS
#define _UNICODE
#include <windows.h>
#include <sddl.h>
#include <taskschd.h>
#include <stdio.h>

#ifdef MEGAPROXY_DEV
#define APP_NAME L"WindowsMegaProxy.Dev"
#define DISPLAY_NAME L"MegaProxy DEV"
#else
#define APP_NAME L"WindowsMegaProxy"
#define DISPLAY_NAME L"MegaProxy"
#endif

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show)
{
    HANDLE token = NULL;
    TOKEN_USER *user = NULL;
    LPWSTR sid = NULL;
    ITaskService *service = NULL;
    ITaskFolder *folder = NULL;
    IRegisteredTask *task = NULL;
    IRunningTask *running = NULL;
    BSTR root = NULL, name = NULL;
    VARIANT empty;
    VariantInit(&empty);
    HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    BOOL initialized = SUCCEEDED(hr);
    DWORD size = 0;
    wchar_t task_name[256];
    if (FAILED(hr)) goto done;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        hr = HRESULT_FROM_WIN32(GetLastError()); goto done;
    }
    GetTokenInformation(token, TokenUser, NULL, 0, &size);
    user = LocalAlloc(LPTR, size);
    if (!user) { hr = E_OUTOFMEMORY; goto done; }
    if (!GetTokenInformation(token, TokenUser, user, size, &size) ||
        !ConvertSidToStringSidW(user->User.Sid, &sid)) {
        hr = HRESULT_FROM_WIN32(GetLastError()); goto done;
    }
    swprintf(task_name, 256, APP_NAME L".User.%ls", sid);
    root = SysAllocString(L"\\");
    name = SysAllocString(task_name);
    if (!root || !name) { hr = E_OUTOFMEMORY; goto done; }
    hr = CoCreateInstance(&CLSID_TaskScheduler, NULL, CLSCTX_INPROC_SERVER,
                          &IID_ITaskService, (void **)&service);
    if (FAILED(hr)) goto done;
    hr = ITaskService_Connect(service, empty, empty, empty, empty);
    if (FAILED(hr)) goto done;
    hr = ITaskService_GetFolder(service, root, &folder);
    if (FAILED(hr)) goto done;
    hr = ITaskFolder_GetTask(folder, name, &task);
    if (FAILED(hr)) goto done;
    hr = IRegisteredTask_Run(task, empty, &running);
done:
    if (running) IRunningTask_Release(running);
    if (task) IRegisteredTask_Release(task);
    if (folder) ITaskFolder_Release(folder);
    if (service) ITaskService_Release(service);
    SysFreeString(name);
    SysFreeString(root);
    LocalFree(sid);
    LocalFree(user);
    if (token) CloseHandle(token);
    if (initialized) CoUninitialize();
    if (FAILED(hr)) {
        wchar_t message[1024], detail[512] = L"";
        FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                       NULL, (DWORD)hr, 0, detail, 512, NULL);
        swprintf(message, 1024, L"Could not start " DISPLAY_NAME
                 L". Repair the current-user installation.\n\nError 0x%08lX\n%ls", (unsigned long)hr, detail);
        MessageBoxW(NULL, message, DISPLAY_NAME, MB_OK | MB_ICONERROR);
        return 1;
    }
    return 0;
}
