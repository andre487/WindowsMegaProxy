import sys
from common import cli, dotnet

if __name__ == "__main__":
    cli(lambda: dotnet(*sys.argv[1:]))
