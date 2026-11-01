// Serves this plugin's repository over HTTP so market/index.json can be previewed
// with the real DSH reader. Usage: node tools/serve-market.cjs [port]
const http = require("http");
const fs = require("fs");
const path = require("path");

const root = path.dirname(__dirname);
const port = Number(process.argv[2] ?? 8199);

const TYPES = {
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".md": "text/markdown",
};

http
  .createServer((request, response) => {
    const urlPath = decodeURIComponent((request.url ?? "/").split("?")[0]);
    const file = path.join(root, urlPath);
    if (!file.startsWith(root)) {
      response.writeHead(403);
      response.end("outside repository");
      return;
    }
    fs.readFile(file, (error, data) => {
      if (error !== null) {
        response.writeHead(404, { "content-type": "text/plain" });
        response.end(`not found: ${urlPath}`);
        return;
      }
      response.writeHead(200, { "content-type": TYPES[path.extname(file)] ?? "application/octet-stream" });
      response.end(data);
    });
  })
  .listen(port, "127.0.0.1", () => {
    console.log(`market index: http://127.0.0.1:${port}/market/index.json`);
  });
