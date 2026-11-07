"""Stop production MegaProxy without changing its saved startup preferences."""
import ctypes as ct

from install import elevate, elevated_cli
from run import stop_workspace_app
from service_control import stop_service, require_production_stopped


def main():
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate(__file__)
        return
    stop_workspace_app(dev=False, workspace_only=False)
    stop_service("WindowsMegaProxy")
    require_production_stopped()
    print("Production MegaProxy stopped. Saved startup preferences were preserved.")


if __name__ == "__main__":
    elevated_cli(main)
