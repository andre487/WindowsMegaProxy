import build_core
from common import ROOT, SDK_VERSION, cli, download, extract
from dependencies import asset, install_core, release


def main():
    # Keep development dependencies in the checkout, even with a global SDK installed.
    if not (ROOT / f".tools/dotnet/sdk/{SDK_VERSION}/dotnet.dll").exists():
        sdk = next(sdk for sdk in release()["sdks"] if sdk["version"] == SDK_VERSION)
        file = asset(sdk, ".zip")
        archive = download(file["url"], ROOT / f".tools/downloads/dotnet-sdk-{SDK_VERSION}.zip", file["hash"], "sha512")
        extract(archive, ROOT / ".tools/dotnet")
    install_core(ROOT / ".tools/sing-box", ROOT / ".tools/downloads")
    install_core(ROOT / ".tools/sing-box-h3", ROOT / ".tools/downloads", http3=True)
    for folder, experimental in (("sing-box", False), ("sing-box-h3", True)):
        build_core.install(ROOT / ".tools/dev-cores" / folder, experimental=experimental, dev=True)
    print("Development dependencies ready in .tools. Run build.cmd and test.cmd.")


if __name__ == "__main__":
    cli(main)
