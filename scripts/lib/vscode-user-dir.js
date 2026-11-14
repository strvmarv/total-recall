// Shared helper: VS Code's per-user data directory (the one that holds mcp.json and
// globalStorage/), used by the VS Code step and by the Cline step.
//
//   VSCODE_USER_DIR when set, else
//   macOS    ~/Library/Application Support/Code/User
//   Windows  %APPDATA%\Code\User   (null when APPDATA is unset)
//   other    ~/.config/Code/User
//
// Returns null when there is no home directory to start from. The caller passes
// `home` as `env.USERPROFILE || homedir()`; the defaults do the same.

import { homedir } from 'node:os';
import { join } from 'node:path';

export function vscodeUserDir({ env = process.env, platform = process.platform, home = env.USERPROFILE || homedir() } = {}) {
  if (env.VSCODE_USER_DIR) return env.VSCODE_USER_DIR;

  if (!home) return null;

  if (platform === 'darwin') {
    return join(home, 'Library', 'Application Support', 'Code', 'User');
  }

  if (platform === 'win32') {
    if (env.APPDATA) return join(env.APPDATA, 'Code', 'User');
    return null;
  }

  // Linux / other
  return join(home, '.config', 'Code', 'User');
}
