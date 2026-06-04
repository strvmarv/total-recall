"""TotalRecallProvider — MemoryProvider ABC implementation for total-recall.

This provider connects to a total-recall MCP server (``total-recall`` binary)
and delegates all memory operations through it.  It replaces the built-in
Hermes memory tool with an identical schema so the agent's workflow doesn't
change — writes land in total-recall's SQLite database instead of local
MEMORY.md / USER.md files.

Design
------

* The ``McpClient`` manages a subprocess MCP server via JSON-RPC over stdio.
  It is imported from ``.mcp_client`` (a sibling module in the plugin
  package).

* McpClient.call_tool always returns a dict (never raises).  On success the
  dict has ``success=True`` plus either a ``text`` key (raw string) or
  JSON-parsed fields from the tool output.  On failure it has
  ``success=False`` and an ``error`` key.

* ``sync_turn`` and ``on_memory_write`` are fire-and-forget — they silently
  drop errors so a slow or unreachable backend doesn't block the turn.
"""

from __future__ import annotations

import json
import logging
import shutil
from typing import Any, Dict, List, Optional

from agent.memory_provider import MemoryProvider
from tools.registry import tool_error

from .mcp_client import McpClient
from .memory_tool_schema import MEMORY_TOOL_SCHEMA

logger = logging.getLogger(__name__)

# Header block that wraps the session context returned by total-recall.
SYSTEM_PROMPT_HEADER = (
    "══════════════════════════════════════════════\n"
    "TOTAL-RECALL HOT TIER (injected automatically)\n"
    "══════════════════════════════════════════════"
)


class TotalRecallProvider(MemoryProvider):
    """Memory provider backed by a local total-recall MCP server."""

    def __init__(self) -> None:
        self._client: Optional[McpClient] = None
        self._session_id: str = ""
        # Cached context block for prefix-cache stability.
        self._cached_context: Optional[str] = None
        self._session_ended: bool = False

    # ------------------------------------------------------------------
    # Properties
    # ------------------------------------------------------------------

    @property
    def name(self) -> str:
        return "total-recall"

    # ------------------------------------------------------------------
    # is_available
    # ------------------------------------------------------------------

    def is_available(self) -> bool:
        """Check that the total-recall binary is on PATH."""
        return shutil.which("total-recall") is not None

    # ------------------------------------------------------------------
    # initialize / shutdown
    # ------------------------------------------------------------------

    def initialize(self, session_id: str, **kwargs) -> None:
        """Start the MCP client and signal a new session.

        ``kwargs`` include the standard MemoryProvider keys (hermes_home,
        platform, agent_context, etc.) — unused here but accepted for
        interface compatibility.
        """
        self._session_id = session_id
        self._cached_context = None  # invalidate across sessions

        # Clean up any prior client to avoid subprocess leaks.
        self.shutdown()

        try:
            self._client = McpClient()
            start_result = self._client.start()
            if start_result.get("success"):
                self._client.call_tool("session_start", {})
                logger.info("total-recall: session started (session_id=%s)", session_id)
            else:
                logger.error("total-recall: start failed — %s", start_result.get("error"))
                self._client = None
        except Exception:
            logger.exception("total-recall: initialize failed — provider will be a no-op")
            self._client = None

    def shutdown(self) -> None:
        """End the current session and stop the MCP client."""
        if self._client is None:
            return
        if not self._session_ended:
            try:
                self._client.call_tool("session_end", {})
            except Exception:
                logger.debug("total-recall: session_end call failed during shutdown", exc_info=True)
            self._session_ended = True
        try:
            self._client.stop()
        except Exception:
            logger.debug("total-recall: client stop failed during shutdown", exc_info=True)
        self._client = None
        self._cached_context = None

    # ------------------------------------------------------------------
    # system_prompt_block
    # ------------------------------------------------------------------

    def system_prompt_block(self) -> str:
        """Return the hot-tier context block, cached for prefix-cache stability."""
        if self._cached_context is not None:
            return self._cached_context

        if self._client is None:
            return ""

        try:
            result = self._client.call_tool("session_context", {})
            context_text = self._extract_text(result)
            if context_text:
                self._cached_context = f"{SYSTEM_PROMPT_HEADER}\n{context_text}"
            else:
                self._cached_context = ""
        except Exception:
            logger.debug("total-recall: system_prompt_block failed", exc_info=True)
            self._cached_context = ""

        return self._cached_context

    # ------------------------------------------------------------------
    # prefetch
    # ------------------------------------------------------------------

    def prefetch(self, query: str, *, session_id: str = "") -> str:
        """Search total-recall for relevant context before a turn."""
        if self._client is None or not query.strip():
            return ""

        try:
            result = self._client.call_tool("memory_search", {
                "query": query,
                "topK": 5,
            })
            items = self._extract_search_results(result)
            if not items:
                return ""
            return "\n".join(f"- {item}" for item in items)
        except Exception:
            logger.debug("total-recall: prefetch failed", exc_info=True)
            return ""

    # ------------------------------------------------------------------
    # sync_turn (fire-and-forget)
    # ------------------------------------------------------------------

    def sync_turn(
        self,
        user_content: str,
        assistant_content: str,
        *,
        session_id: str = "",
        messages: Optional[List[Dict[str, Any]]] = None,
    ) -> None:
        """Store the turn as a Surfaced entry.  Fire-and-forget — never raises."""
        if self._client is None:
            return

        combined = f"User: {user_content}\nAssistant: {assistant_content}"
        try:
            self._client.call_tool("memory_store", {
                "content": combined,
                "tags": ["hermes-turn"],
                "entryType": "surfaced",
            })
        except Exception:
            logger.debug("total-recall: sync_turn failed", exc_info=True)

    # ------------------------------------------------------------------
    # on_session_end
    # ------------------------------------------------------------------

    def on_session_end(self, messages: List[Dict[str, Any]]) -> None:
        """Signal end of session to total-recall.  Best-effort."""
        if self._client is None or self._session_ended:
            return
        try:
            self._client.call_tool("session_end", {})
            self._session_ended = True
        except Exception:
            logger.debug("total-recall: on_session_end failed", exc_info=True)

    # ------------------------------------------------------------------
    # on_memory_write (fire-and-forget mirror)
    # ------------------------------------------------------------------

    def on_memory_write(
        self,
        action: str,
        target: str,
        content: str,
        metadata: Optional[Dict[str, Any]] = None,
    ) -> None:
        """Mirror built-in memory writes to total-recall.

        Only acts on ``add`` and ``replace`` actions.  The entry type and
        tags are inferred from the ``target`` parameter:

        * ``target == "memory"`` → entry_type=correction, tags=["hermes-memory"]
        * ``target == "user"``   → entry_type=preference, tags=["hermes-user"]
        """
        if self._client is None or action not in {"add", "replace"}:
            return

        entry_type, tags = self._target_to_entry_type(target)

        try:
            self._client.call_tool("memory_store", {
                "content": content,
                "tags": tags,
                "entryType": entry_type,
            })
        except Exception:
            logger.debug("total-recall: on_memory_write failed", exc_info=True)

    # ------------------------------------------------------------------
    # get_tool_schemas
    # ------------------------------------------------------------------

    def get_tool_schemas(self) -> List[Dict[str, Any]]:
        """Return the replacement memory tool schema."""
        return [MEMORY_TOOL_SCHEMA]

    # ------------------------------------------------------------------
    # handle_tool_call — replacement memory tool
    # ------------------------------------------------------------------

    def handle_tool_call(self, tool_name: str, args: Dict[str, Any], **kwargs) -> str:
        """Dispatch ``memory`` tool calls to total-recall.

        Action mapping:

        * ``add``     → ``memory_store`` (tags + entry_type from target)
        * ``replace`` → ``memory_search`` old → ``memory_delete`` old +
                         ``memory_store`` new
        * ``remove``  → ``memory_search`` old → ``memory_delete``
        * ``read``    → ``memory_recent`` (limit=50)
        """
        if tool_name != "memory":
            return tool_error(f"Provider '{self.name}' does not handle tool '{tool_name}'")

        if self._client is None:
            return tool_error("total-recall is not available")

        action: str = args.get("action", "")
        target: str = args.get("target", "memory")
        content: str = args.get("content", "")
        old_text: str = args.get("old_text", "")

        try:
            if action == "add":
                return self._handle_add(target, content)

            elif action == "replace":
                return self._handle_replace(target, old_text, content)

            elif action == "remove":
                return self._handle_remove(target, old_text)

            elif action == "read":
                return self._handle_read()

            else:
                return tool_error(f"Unknown action '{action}'. Use: add, replace, remove, read")

        except Exception as exc:
            logger.exception("total-recall: handle_tool_call failed for action=%s", action)
            return tool_error(f"total-recall tool error: {exc}")

    # ------------------------------------------------------------------
    # Internal action helpers
    # ------------------------------------------------------------------

    def _handle_add(self, target: str, content: str) -> str:
        if not content.strip():
            return tool_error("Content is required for 'add' action.", success=False)

        entry_type, tags = self._target_to_entry_type(target)

        result = self._client.call_tool("memory_store", {
            "content": content,
            "tags": tags,
            "entryType": entry_type,
        })
        return self._tool_result_json(result)

    def _handle_replace(self, target: str, old_text: str, content: str) -> str:
        if not old_text.strip():
            return tool_error("old_text is required for 'replace' action.", success=False)
        if not content.strip():
            return tool_error("content is required for 'replace' action.", success=False)

        # Find the old entry via search.
        search_result = self._client.call_tool("memory_search", {
            "query": old_text,
            "topK": 20,
        })

        old_id = self._find_entry_id_by_text(search_result, old_text)
        if old_id is None:
            return json.dumps({
                "success": False,
                "error": f"No entry matched '{old_text}'.",
            }, ensure_ascii=False)

        # Delete the old entry.
        self._client.call_tool("memory_delete", {"id": old_id})

        # Store the new entry.
        entry_type, tags = self._target_to_entry_type(target)

        result = self._client.call_tool("memory_store", {
            "content": content,
            "tags": tags,
            "entryType": entry_type,
        })
        return self._tool_result_json(result)

    def _handle_remove(self, target: str, old_text: str) -> str:
        if not old_text.strip():
            return tool_error("old_text is required for 'remove' action.", success=False)

        # Find the old entry via search.
        search_result = self._client.call_tool("memory_search", {
            "query": old_text,
            "topK": 20,
        })

        old_id = self._find_entry_id_by_text(search_result, old_text)
        if old_id is None:
            return json.dumps({
                "success": False,
                "error": f"No entry matched '{old_text}'.",
            }, ensure_ascii=False)

        # Delete it.
        self._client.call_tool("memory_delete", {"id": old_id})
        return json.dumps(
            {"success": True, "message": "Entry removed.", "target": target},
            ensure_ascii=False,
        )

    def _handle_read(self) -> str:
        """Return the 50 most recent memories."""
        result = self._client.call_tool("memory_recent", {"limit": 50})
        return self._tool_result_json(result)

    # ------------------------------------------------------------------
    # Response helpers — adapt McpClient.call_tool return shape
    #
    # McpClient.call_tool returns a flat dict:
    #   On success: {"success": True, "text": "..."}    ← raw text response
    #   On success: {"success": True, "key": val, ...}  ← JSON-parsed content
    #   On failure: {"success": False, "error": "..."}
    # ------------------------------------------------------------------

    @staticmethod
    def _target_to_entry_type(target: str) -> tuple[str, list[str]]:
        """Map a Hermes memory target to a total-recall entry type and tags.

        * ``target == "user"``   → entry_type="preference", tags=["hermes-user"]
        * otherwise              → entry_type="correction", tags=["hermes-memory"]
        """
        if target == "user":
            return "preference", ["hermes-user"]
        return "correction", ["hermes-memory"]

    @staticmethod
    def _extract_text(result: Dict[str, Any]) -> str:
        """Pull the text content from an McpClient.call_tool success response."""
        if not result.get("success"):
            return ""
        # If JSON-parsed, the dict already contains the tool result keys.
        # Use the "text" field for raw-text responses; otherwise return
        # the whole dict as a JSON string for downstream parsing.
        if "text" in result:
            return result["text"]
        # The call_tool result has the tool payload inlined.
        # Return the JSON-serialised dict so callers can parse it.
        return json.dumps(result, ensure_ascii=False)

    @staticmethod
    def _extract_search_results(result: Dict[str, Any]) -> List[str]:
        """Extract human-readable previews from a memory_search result."""
        if not result.get("success"):
            return []

        # McpClient already JSON-parses the tool output, so the result
        # dict may have inline fields like "entries", "results", or a
        # "text" / "data" key containing the parsed payload.
        data = TotalRecallProvider._get_payload(result)

        items: List[str] = []
        if isinstance(data, list):
            entries = data
        elif isinstance(data, dict):
            entries = data.get("entries", data.get("results", []))
        else:
            return items

        for entry in entries:
            if not isinstance(entry, dict):
                continue
            preview = entry.get("preview") or entry.get("content") or ""
            score = entry.get("score")
            if isinstance(preview, str) and preview.strip():
                line = preview.strip()
                if score is not None:
                    line = f"[{score:.2f}] {line}"
                items.append(line)
        return items

    @staticmethod
    def _find_entry_id_by_text(result: Dict[str, Any], text: str) -> Optional[str]:
        """Find an entry id whose content contains ``text``.

        Searches the parsed search result for a matching entry.
        """
        if not result.get("success"):
            return None

        data = TotalRecallProvider._get_payload(result)

        if isinstance(data, list):
            entries = data
        elif isinstance(data, dict):
            entries = data.get("entries", data.get("results", []))
        else:
            return None

        for entry in entries:
            if not isinstance(entry, dict):
                continue
            content_val = entry.get("content") or entry.get("preview") or ""
            if isinstance(content_val, str) and text.lower() in content_val.lower():
                # Prefer the top-level "id" or "entry.id".
                eid = entry.get("id")
                if eid is None and isinstance(entry.get("entry"), dict):
                    eid = entry["entry"].get("id")
                if eid:
                    return str(eid)

        return None

    @staticmethod
    def _get_payload(result: Dict[str, Any]) -> Any:
        """Extract the actual tool-result payload from a call_tool success dict.

        McpClient.call_tool either inlines the JSON-parsed content into the
        response dict (e.g. ``{"success": True, "id": "...", "entries": [...]}``)
        or wraps raw text in a ``"text"`` key, or puts structured data into a
        ``"data"`` key.
        """
        if "data" in result:
            return result["data"]
        if "text" in result:
            raw = result["text"]
            try:
                return json.loads(raw)
            except (json.JSONDecodeError, TypeError):
                return raw
        # Return the result dict minus the "success" envelope key.
        return {k: v for k, v in result.items() if k != "success"}

    @staticmethod
    def _tool_result_json(result: Dict[str, Any]) -> str:
        """Convert an McpClient.call_tool result to a JSON tool-result string."""
        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "unknown error"),
            }, ensure_ascii=False)

        payload = TotalRecallProvider._get_payload(result)
        if isinstance(payload, dict):
            return json.dumps(payload, ensure_ascii=False)
        if isinstance(payload, (list, str, int, float, bool)) or payload is None:
            return json.dumps({"result": payload}, ensure_ascii=False)
        return json.dumps({"success": True}, ensure_ascii=False)
