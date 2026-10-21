"""A tiny OpenAI-compatible server for end-to-end tests: streams text, reasoning and tool calls."""
import json, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def send_json(self, obj, status=200):
        b = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        if self.path.endswith("/models"):
            self.send_json({"data": [{"id": "mock-model"}, {"id": "mock-vision-model"}]})
        else:
            self.send_json({"error": {"message": "not found"}}, 404)

    def chunk(self, delta, usage=None):
        obj = {"choices": [{"index": 0, "delta": delta}]}
        if usage: obj["usage"] = usage
        self.wfile.write(f"data: {json.dumps(obj)}\n\n".encode())
        self.wfile.flush()

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(n) or b"{}")
        with open(sys.argv[2] if len(sys.argv) > 2 else "/dev/null", "a") as f:
            f.write(json.dumps(body) + "\n")
        msgs = body.get("messages", [])
        last = msgs[-1] if msgs else {}
        content = last.get("content")
        text = content if isinstance(content, str) else " ".join(p.get("text", "") for p in (content or []) if isinstance(p, dict))
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        if last.get("role") == "user" and "files" in text.lower() and body.get("tools"):
            self.chunk({"role": "assistant", "content": None, "tool_calls": [{"index": 0, "id": "call_1", "type": "function", "function": {"name": "list_files", "arguments": ""}}]})
            for part in ['{"pa', 'th": ".', '"}']:
                self.chunk({"tool_calls": [{"index": 0, "function": {"arguments": part}}]})
        elif last.get("role") == "tool":
            for w in ["I looked at the folder. ", "It contains **", str(last.get("content", "")).count("\n"), "** entries."]:
                self.chunk({"content": str(w)}); time.sleep(0.05)
        else:
            self.chunk({"reasoning_content": "Let me think about this carefully."})
            time.sleep(1.2)
            answer = ["Here is a **demo** answer with a list:\n\n", "- one\n- two\n\n", "```python\nprint('hi')\n```\n", "\n| a | b |\n|---|---|\n| 1 | 2 |\n"]
            for a in answer:
                self.chunk({"content": a}); time.sleep(0.25)
        self.chunk({}, usage={"prompt_tokens": 42, "completion_tokens": 17})
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
