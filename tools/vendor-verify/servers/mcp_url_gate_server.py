"""stdio MCP server that drives SEP-1036 URL-mode elicitation END TO END, for #531.

WHY THIS IS SEPARATE FROM mcp_elicit_server.py
----------------------------------------------
That server measures whether a url-mode request is *accepted and routed*, headless, where nobody
can answer. This one measures the round trip, and it needs three things that one does not have:

  * a REAL http endpoint, so "the user opened the URL" is an observable event rather than an
    assumption. The other server hands out `https://localhost:9/...` -- port 9 is discard, chosen
    deliberately so nothing could be reached.
  * `notifications/elicitation/complete`, which is what tells the client the out-of-band work is
    finished.
  * the `URLElicitationRequiredError` (-32042) flow, where the server does NOT hold the tool call.

A CORRECTION THE SPEC FORCED
----------------------------
`mcp_elicit_server.py` treats `action: "accept"` as approval and completes the tool call. For form
mode that is right. For URL mode it is WRONG, and the specification says so outright:

    "The response with action: accept indicates that the user has consented to the interaction.
     It does not mean that the interaction is complete. The interaction occurs out of band and the
     client is not aware of the outcome until and unless the server sends a notification."

So `accept` here means only "I will open that URL". Completing the tool call on it would measure a
gate that opens on consent rather than on the answer -- the precise failure this whole suite exists
to catch, and it would have looked like a pass.

THE TWO FLOWS, AND WHY THE SECOND ONE IS THE POINT
--------------------------------------------------
AER_URL_FLOW=hold      server keeps tools/call open, completes it after the URL is hit.
AER_URL_FLOW=required  server answers tools/call IMMEDIATELY with -32042 and a list of required
                       elicitations, then waits. This is the flow decision 0029 needs: the call is
                       released, the human answers whenever, and the client retries. The spec marks
                       that retry **MAY**, so whether `agy` actually does it is the open question.

SENTINELS -- every one a FILE, because a TUI's account of itself is not evidence
-------------------------------------------------------------------------------
  CAPS.json        the client's declared capabilities at initialize
  ELICITED.json    the request was issued, and the client's three-action response
  URL_HIT.json     the http endpoint was actually opened, and by what user-agent
  NOTIFIED.json    notifications/elicitation/complete was sent
  RETRIED.json     the client called the tool AGAIN after the notification  <-- the #531 answer
  CALLED_<tool>    the tool body ran to completion
"""
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer

D = os.environ.get("AER_SENTINEL_DIR", ".")
FLOW = os.environ.get("AER_URL_FLOW", "hold")        # "hold" | "required"
EID = "aer-531-0001"

_out_lock = threading.Lock()
_state = {"pending_call": None, "calls": 0, "hit": False, "consented": False}


def send(o):
    with _out_lock:
        sys.stdout.write(json.dumps(o) + "\n")
        sys.stdout.flush()


def write(name, obj):
    with open(os.path.join(D, name), "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2) if not isinstance(obj, str) else f.write(obj)


# --------------------------------------------------------------------------- the out-of-band side
class Gate(BaseHTTPRequestHandler):
    def do_GET(self):                                    # noqa: N802 -- BaseHTTPRequestHandler API
        _state["hit"] = True
        write("URL_HIT.json", {"path": self.path,
                               "user_agent": self.headers.get("User-Agent"),
                               "at": time.time()})
        body = b"<html><body><h1>AER gate: approved</h1></body></html>"
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

        # The interaction is now complete out of band. Tell the client -- and, in the holding
        # flow, only NOW may the tool call be answered.
        send({"jsonrpc": "2.0", "method": "notifications/elicitation/complete",
              "params": {"elicitationId": EID}})
        write("NOTIFIED.json", {"elicitationId": EID, "flow": FLOW, "at": time.time()})
        call_id = _state.pop("pending_call", None)
        if call_id is not None:
            write("CALLED_elicit_tool", "1")
            send({"jsonrpc": "2.0", "id": call_id, "result": {
                "content": [{"type": "text", "text": "completed after out-of-band approval"}]}})

    def log_message(self, *a):                           # noqa: A003 -- silence the access log
        pass


_httpd = HTTPServer(("127.0.0.1", 0), Gate)
PORT = _httpd.server_address[1]
threading.Thread(target=_httpd.serve_forever, daemon=True).start()
URL = "http://127.0.0.1:%d/aer-gate/%s" % (PORT, EID)
write("URL.txt", URL)          # so the driver can curl it without scraping the TUI

TOOLS = [
    {"name": "control_tool",
     "description": "Records a control marker. Requests nothing from the user.",
     "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False}},
    {"name": "elicit_tool",
     "description": "Requires out-of-band approval at a URL before it can complete.",
     "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False}},
]

ELICIT_PARAMS = {"mode": "url", "elicitationId": EID, "url": URL,
                 "message": "AER gate: approve this operation in your browser."}


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
    except ValueError:
        continue
    method, rid = req.get("method"), req.get("id")

    # The client's answer to OUR elicitation/create. Per spec this is CONSENT TO OPEN THE URL and
    # nothing more -- it must not complete the tool call. Recorded, then deliberately dropped.
    if method is None and rid == 90001:
        result = req.get("result") or {}
        _state["consented"] = result.get("action") == "accept"
        write("ELICITED.json", {"issued": True, "flow": FLOW, "url": URL,
                                "response": req.get("result", req.get("error")),
                                "note": "accept == consent to open the URL, NOT completion"})
        continue

    if method == "initialize":
        params = req.get("params", {}) or {}
        write("CAPS.json", {"clientInfo": params.get("clientInfo"),
                            "protocolVersion": params.get("protocolVersion"),
                            "capabilities": params.get("capabilities")})
        send({"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": params.get("protocolVersion", "2025-11-25"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "aer-url-gate-probe", "version": "1.0.0"}}})
    elif method == "tools/list":
        send({"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}})
    elif method == "tools/call":
        name = (req.get("params", {}) or {}).get("name", "?")
        if name != "elicit_tool":
            write("CALLED_%s" % name, "1")
            send({"jsonrpc": "2.0", "id": rid, "result": {
                "content": [{"type": "text", "text": "%s executed" % name}]}})
            continue

        _state["calls"] += 1
        # A SECOND call to the gated tool is the whole question: after the completion
        # notification, did the client retry on its own? The spec marks that MAY.
        if _state["calls"] > 1:
            write("RETRIED.json", {"call_number": _state["calls"], "after_hit": _state["hit"],
                                   "at": time.time()})
            if _state["hit"]:
                write("CALLED_elicit_tool", "1")
                send({"jsonrpc": "2.0", "id": rid, "result": {
                    "content": [{"type": "text", "text": "completed on retry after approval"}]}})
                continue

        if FLOW == "required":
            # The non-blocking shape: answer NOW with the error, hold nothing open, and let the
            # human take as long as they like. This is the property 0029 is built around.
            #
            # No elicitation/create is sent in this flow -- the elicitation travels INSIDE the
            # error -- so nothing would ever write ELICITED.json. Write it here, or "the request
            # was never issued" and "the client ignored it" become indistinguishable.
            write("ELICITED.json", {"issued": True, "flow": FLOW, "url": URL,
                                    "via": "URLElicitationRequiredError (-32042)",
                                    "response": None,
                                    "note": "delivered in the error's data.elicitations, per SEP"})
            send({"jsonrpc": "2.0", "id": rid, "error": {
                "code": -32042,
                "message": "This request requires more information.",
                "data": {"elicitations": [ELICIT_PARAMS]}}})
        else:
            _state["pending_call"] = rid          # answered only when the URL is hit
            send({"jsonrpc": "2.0", "id": 90001, "method": "elicitation/create",
                  "params": ELICIT_PARAMS})
    elif method and method.startswith("notifications/"):
        pass
    elif rid is not None:
        send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "not found"}})
