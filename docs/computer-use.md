# Computer Use application access

Select Codex, connect to a local working DWG, then choose **Computer Use: access**
in the CadMcp panel. Windows checks the connected `acad.exe` signature and the
panel displays its publisher, product and path. Choose **Allow from next turn**,
then start a new turn. Granting access never replays a failed tool call or CAD
operation. Claude does not expose this unsupported action.

The rule uses the CLI's publisher/product/binary identity, so it can cover other
installed AutoCAD versions sharing that identity. It is not a per-document or
filesystem sandbox. Do not use GUI automation to edit another drawing or bypass
the OneDrive workspace ban. A connected OneDrive working drawing receives no
Computer Use grant from the panel.

**Revoke Computer Use** works even when AutoCAD is disconnected. It revokes the
CadMcp grants and stops active Codex providers through a per-provider monitor,
including hidden tabs. Actions already started and previously accepted native
CAD operations must be reconciled separately; revocation is not rollback.

Grants live in `%LOCALAPPDATA%/CadMcp/computer-use-access.json`, independently of
chat history. A changed executable hash or identity requires a new grant. Writes
are serialized across panels; malformed or unreadable grants fail closed.

Codex 0.159.0 supports `computer_use.default_app_access` and
`computer_use.windows.exes` with `publisher_name`, `product_name`, `binary_name`
and `access`. CadMcp sets process and new/resumed-thread overrides, validates
`config/read` before starting a model turn, and denies pre-existing packaged-app
grants in thread overrides. Reviewer configurations deny executable access.
The user's global `config.toml` is not edited. Unsupported or overridden settings
fail visibly; administrative requirements are not bypassed.

The grant does not install Computer Use or establish that the account has the
tool, Windows permits input to an elevated window, or organizational policy
allows the action. The panel reports configuration acknowledgement separately
from actual tool success. GUI actions have no native CAD transaction receipt.
C# permission remains a separate checkbox.

The schema and config/read were checked with the checksum-verified official
0.159.0 app-server binary. Managed fixtures cover the protocol and revocation;
WPF probes exercise dialog buttons and screenshots. Real Windows Computer Use
control of an installed AutoCAD and the publisher identity match need native
validation.
