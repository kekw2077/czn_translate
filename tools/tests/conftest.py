import sys
from pathlib import Path

# The conveyor is a set of scripts rather than an installed package, so tests import it the same
# way the scripts do: from the tools/ directory.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
