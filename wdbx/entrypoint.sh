#!/usr/bin/env bash
# Runs the WDBXEditor CLI under Mono (native arch, no Wine). All arguments pass straight through:
#   entrypoint.sh -import -f "Spell.dbc" -b 12340 -c "Spell.txt" -h true -u Update -i TakeNewest
#
# The -import / -export CLI paths are pure managed code and don't open the GUI, so no X server is
# needed. MONO_WINFORMS_XIM_STYLE avoids a needless X input-method probe during WinForms static init.
set -euo pipefail

export MONO_WINFORMS_XIM_STYLE=disabled

exec mono "/opt/wdbx/WDBX Editor.exe" "$@"
