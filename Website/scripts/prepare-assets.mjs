import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = resolve(root, "public/resources/tor-pos-logo.jpg.b64");
const target = resolve(root, "public/resources/tor-pos-logo.jpg");

mkdirSync(dirname(target), { recursive: true });
const payload = readFileSync(source, "utf8").replace(/\s+/g, "");
writeFileSync(target, Buffer.from(payload, "base64"));
console.log("Prepared public/resources/tor-pos-logo.jpg");
