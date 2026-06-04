"""
mcp_client.py — thin MCP JSON-RPC client over stdio subprocess.

Talks to `total-recall serve` (or `total-recall` with no args) via
stdin/stdout using the JSON-RPC 2.0 wire protocol.  Stdlib-only,
Python 3.11+.

MCP methods used:
    initialize       (handshake — request)
    tools/call        (all operations)
    notifications/initialized  (post-handshake notification)

Wire shapes (mirrors TotalRecall.Server.JsonContext.cs):
    Request:  {"jsonrpc":"2.0","id":N,"method":"...","params":{...}}
    Response: {"jsonrpc":"2.0","id":N,"result":{...}}
              {"jsonrpc":"2.0","id":N,"error":{"code":...,"message":"..."}}
    Notification: {"jsonrpc":"2.0","method":"...","params":{...}}

Every public method returns a dict — never raises on tool-call errors.
"""

from __future__ import annotations

import json
import logging
import subprocess
import threading
import time
from typing import Any

logger = logging.getLogger(__name__)

# ── public API ──────────────────────────────────────────────────────────


class McpClient:
    """Thin MCP JSON-RPC client that manages a `total-recall serve` subprocess.

    Parameters
    ----------
    executable : str
        Path to the ``total-recall`` binary (or just ``total-recall`` if it's
        on PATH).
    timeout : float
        Default timeout in seconds for each JSON-RPC call (default 30).
    """

    def __init__(self, executable: str = "total-recall", timeout: float = 30.0) -> None:
        import shutil
        resolved = shutil.which(executable)
        if resolved is None:
            resolved = executable  # fall back to bare name, let Popen fail
        self._executable = resolved
        self._timeout = timeout
        self._proc: subprocess.Popen[str] | None = None
        self._lock = threading.Lock()
        self._request_id = 0

    # ── lifecycle ──────────────────────────────────────────────────

    def start(self) -> dict[str, Any]:
        """Spawn the subprocess and perform the MCP initialize handshake.

        Returns a dict with ``success`` (bool) and either ``result`` (the
        parsed initialize response) or ``error``.
        """
        with self._lock:
            if self._proc is not None and self._proc.poll() is None:
                logger.debug("McpClient.start: process already running")
                return {"success": True, "result": "already_running"}

            try:
                self._proc = subprocess.Popen(
                    [self._executable, "serve"],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    text=True,
                    bufsize=1,  # line buffered
                )
            except FileNotFoundError:
                self._proc = None
                logger.warning("total-recall executable not found: %s", self._executable)
                return {"success": False, "error": f"executable not found: {self._executable}"}
            except Exception as exc:
                self._proc = None
                logger.warning("Failed to start %s: %s", self._executable, exc)
                return {"success": False, "error": str(exc)}

            # Handshake: initialize request → read response → send initialized
            self._request_id = 0
            init_id = self._next_id()
            self._send_request("initialize", {
                "protocolVersion": "2024-11-05",
                "clientInfo": {"name": "hermes-mcp-client", "version": "0.1.0"},
                "capabilities": {},
            }, request_id=init_id)

            resp = self._read_response(timeout=5.0)
            if resp is None:
                logger.warning("McpClient.start: no response to initialize")
                self._stop_nolock()
                return {"success": False, "error": "no response to initialize"}

            if "error" in resp:
                logger.warning("McpClient.start: initialize error: %s", resp["error"])
                self._stop_nolock()
                return {"success": False, "error": resp["error"]}

            # Check that the response id matches
            if resp.get("id") != init_id:
                logger.warning("McpClient.start: id mismatch (expected %s, got %s)", init_id, resp.get("id"))
                self._stop_nolock()
                return {"success": False, "error": "initialize id mismatch"}

            # Send initialized notification
            self._send_notification("notifications/initialized", {})

            result = resp.get("result", {})
            logger.info("McpClient.start: handshake complete. server=%s",
                        result.get("serverInfo", {}).get("name", "unknown"))
            return {"success": True, "result": result}

    def stop(self) -> None:
        """Close stdin, terminate the subprocess, and wait for it to exit."""
        with self._lock:
            self._stop_nolock()

    def _stop_nolock(self) -> None:
        """Internal cleanup.  Caller MUST hold self._lock."""
        proc = self._proc
        self._proc = None

        if proc is None:
            return

        try:
            if proc.stdin is not None:
                proc.stdin.close()
        except Exception:
            pass

        try:
            proc.terminate()
        except Exception:
            pass

        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            try:
                proc.kill()
                proc.wait(timeout=5)
            except Exception:
                pass
        except Exception:
            pass

    def is_running(self) -> bool:
        """Return True if the subprocess is alive."""
        proc = self._proc  # atomic read
        return proc is not None and proc.poll() is None

    # ── tool call ──────────────────────────────────────────────────

    def call_tool(self, tool_name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
        """Send a ``tools/call`` request and return the parsed result.

        Auto-starts the subprocess if it is not already running.

        Returns
        -------
        dict
            On success: ``{"success": True, "text": ...}`` or
            ``{"success": True, **json_parsed_content}``.
            On failure: ``{"success": False, "error": "..."}``.
        """
        if not self.is_running():
            start_result = self.start()
            if not start_result.get("success"):
                return start_result

        with self._lock:
            req_id = self._next_id()
            self._send_request("tools/call", {
                "name": tool_name,
                "arguments": arguments or {},
            }, request_id=req_id)

            resp = self._read_response()
        if resp is None:
            return {"success": False, "error": "no response from server (process may have crashed)"}

        if resp.get("id") != req_id:
            logger.warning("call_tool: id mismatch (expected %s, got %s); response: %s",
                           req_id, resp.get("id"), resp)

        if "error" in resp:
            err = resp["error"]
            if isinstance(err, dict):
                return {"success": False, "error": err.get("message", str(err))}
            return {"success": False, "error": str(err)}

        result = resp.get("result", {})
        # Check MCP-level isError flag before processing content
        if result.get("isError"):
            content = result.get("content", [])
            error_text = content[0].get("text", "unknown error") if content else "unknown error"
            return {"success": False, "error": error_text}

        content = result.get("content", [])
        if not content:
            return {"success": True, "text": ""}

        # The first content item is the primary text payload.
        first = content[0]
        raw_text = first.get("text", "") if isinstance(first, dict) else ""

        # Try to parse the text as JSON — total-recall tool results are
        # often JSON-encoded inside the text field.
        try:
            parsed = json.loads(raw_text)
            if isinstance(parsed, dict):
                parsed["success"] = True
                return parsed
            return {"success": True, "text": raw_text, "data": parsed}
        except (json.JSONDecodeError, TypeError):
            return {"success": True, "text": raw_text}

    # ── internals ──────────────────────────────────────────────────

    def _next_id(self) -> int:
        self._request_id += 1
        return self._request_id

    def _send_request(self, method: str, params: dict[str, Any], request_id: int) -> None:
        """Write a JSON-RPC request to the subprocess stdin."""
        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params,
        }
        self._write_line(json.dumps(payload, ensure_ascii=False))

    def _send_notification(self, method: str, params: dict[str, Any]) -> None:
        """Write a JSON-RPC notification (no id field) to the subprocess stdin."""
        payload = {
            "jsonrpc": "2.0",
            "method": method,
            "params": params,
        }
        self._write_line(json.dumps(payload, ensure_ascii=False))

    def _write_line(self, line: str) -> None:
        """Write a single line to the subprocess stdin.  Must hold _lock."""
        proc = self._proc
        if proc is None or proc.stdin is None:
            logger.warning("_write_line: subprocess not available")
            return
        try:
            proc.stdin.write(line + "\n")
            proc.stdin.flush()
        except (BrokenPipeError, OSError) as exc:
            logger.warning("_write_line: write failed: %s", exc)

    def _read_response(self, timeout: float | None = None) -> dict[str, Any] | None:
        """Read one JSON line from stdout, parse, and return as a dict.

        Returns None on timeout, EOF, or parse failure.
        """
        if timeout is None:
            timeout = self._timeout

        proc = self._proc
        if proc is None or proc.stdout is None:
            return None

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            # Poll to detect process crash
            if proc.poll() is not None:
                logger.warning("_read_response: subprocess exited with code %s", proc.returncode)
                return None

            try:
                line = proc.stdout.readline()
            except Exception as exc:
                logger.warning("_read_response: readline error: %s", exc)
                return None

            if not line:
                # EOF — process closed stdout
                logger.warning("_read_response: EOF (process may have crashed)")
                return None

            line = line.strip()
            if not line:
                # Empty line — process may have crashed; check once more
                if proc.poll() is not None:
                    logger.warning("_read_response: empty line + process exited")
                    return None
                time.sleep(0.01)
                continue

            try:
                return json.loads(line)
            except json.JSONDecodeError as exc:
                logger.warning("_read_response: invalid JSON: %s", exc)
                continue

        logger.warning("_read_response: timeout after %.1fs", timeout)
        return None


# ── convenience ─────────────────────────────────────────────────────────


def create_client(executable: str = "total-recall", timeout: float = 30.0) -> McpClient:
    """Factory function — create and start an McpClient.

    Returns the client instance.  Caller should check
    ``client.is_running()`` after creation.
    """
    client = McpClient(executable=executable, timeout=timeout)
    result = client.start()
    if not result.get("success"):
        logger.warning("create_client: start failed: %s", result.get("error"))
    return client
