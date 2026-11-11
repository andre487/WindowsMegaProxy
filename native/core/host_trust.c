#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <softpub.h>
#include "host_certificate.h"

static INIT_ONCE initialized = INIT_ONCE_STATIC_INIT;
static int authorized = 0;

HANDLE megaproxy_verify_file(const WCHAR *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;
#ifdef MEGAPROXY_DEV
    return file;
#else
    int valid = 0;
    WINTRUST_FILE_INFO info = {0};
    info.cbStruct = sizeof(info);
    info.pcwszFilePath = path;
    info.hFile = file;
    WINTRUST_DATA data = {0};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &info;
    data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE;
    GUID policy = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG status = WinVerifyTrust(INVALID_HANDLE_VALUE, &policy, &data);
    // Our self-signed publisher is pinned below; never install it as a machine root.
    if (status == ERROR_SUCCESS || status == CERT_E_UNTRUSTEDROOT) {
        CRYPT_PROVIDER_DATA *provider = WTHelperProvDataFromStateData(data.hWVTStateData);
        CRYPT_PROVIDER_SGNR *signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : NULL;
        PCCERT_CONTEXT pinned = CertCreateCertificateContext(X509_ASN_ENCODING, host_certificate, sizeof(host_certificate));
        if (signer && signer->csCertChain && pinned &&
            (signer->dwError == ERROR_SUCCESS || signer->dwError == CERT_E_UNTRUSTEDROOT)) {
            PCCERT_CONTEXT actual = signer->pasCertChain[0].pCert;
            valid = actual && signer->pChainContext &&
                !(signer->pChainContext->TrustStatus.dwErrorStatus & ~CERT_TRUST_IS_UNTRUSTED_ROOT) &&
                CertComparePublicKeyInfo(X509_ASN_ENCODING,
                &actual->pCertInfo->SubjectPublicKeyInfo, &pinned->pCertInfo->SubjectPublicKeyInfo);
        }
        if (pinned) CertFreeCertificateContext(pinned);
    }
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(INVALID_HANDLE_VALUE, &policy, &data);
    if (!valid) { CloseHandle(file); return INVALID_HANDLE_VALUE; }
    return file;
#endif
}

static BOOL CALLBACK verify_host(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    WCHAR path[32768];
    DWORD length = GetModuleFileNameW(NULL, path, ARRAYSIZE(path));
    if (!length || length >= ARRAYSIZE(path)) return TRUE;
    HANDLE file = megaproxy_verify_file(path);
    authorized = file != INVALID_HANDLE_VALUE;
    if (authorized) CloseHandle(file);
    return TRUE;
}

int megaproxy_authorize_host(void) {
#ifdef MEGAPROXY_DEV
    return 1;
#else
    InitOnceExecuteOnce(&initialized, verify_host, NULL, NULL);
    return authorized;
#endif
}
