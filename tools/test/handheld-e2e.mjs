// El terminali sayfasının uçtan uca testi: görünmez Edge (Chrome DevTools Protocol) + Node'un yerleşik WebSocket'i.
// Kullanım: node handheld-e2e.mjs <cdp-port> <sunucu-adresi> <ekran-görüntüsü-klasörü>
// Çıktı: her satır "GECTI ad" / "KALDI ad"; uitest.ps1 -Part H çağırır (sunucuyu ve verileri o hazırlar).
import { writeFileSync } from "node:fs";
import { join } from "node:path";

const [port, server, shotDir] = process.argv.slice(2);
const results = [];
const check = (name, ok) => results.push(`${ok ? "GECTI" : "KALDI"} ${name}`);
const sleep = ms => new Promise(r => setTimeout(r, ms));

const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const page = targets.find(t => t.type === "page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r, { once: true }));
let nextId = 1;
const pending = new Map();
ws.addEventListener("message", e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
const send = (method, params = {}) => new Promise(r => { const id = nextId++; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
const ev = async expr => (await send("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
const text = id => ev(`document.getElementById(${JSON.stringify(id)}).textContent`);
const cls = id => ev(`document.getElementById(${JSON.stringify(id)}).className`);
const visible = id => ev(`!document.getElementById(${JSON.stringify(id)}).classList.contains("hidden")`);
async function waitFor(fn, ms = 8000) { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await sleep(150); } return false; }
async function type(id, value) {   // okuyucu gibi: odak + metin + Enter
  await ev(`document.getElementById(${JSON.stringify(id)}).focus()`);
  await send("Input.insertText", { text: value });
  for (const type of ["keyDown", "keyUp"])
    await send("Input.dispatchKeyEvent", { type, key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
}
const click = id => ev(`document.getElementById(${JSON.stringify(id)}).click()`);
async function shot(name) {
  const r = await send("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(shotDir, name + ".png"), Buffer.from(r.result.data, "base64"));
}
const api = async path => (await fetch(server + path)).json();
const state = async id => (await api(`api/v1/rolls/${id}`)).roll.state;

// Telefon / el terminali ekranı
await send("Emulation.setDeviceMetricsOverride", { width: 412, height: 915, deviceScaleFactor: 1, mobile: true });
await send("Network.enable");
await send("Page.navigate", { url: server + "el-terminali" });
await sleep(1500);

check("Sayfa acildi (giris)", await visible("login"));
await type("badge", "P0001");
await type("pin", "0000");
check("Yanlis PIN uyarisi", await waitFor(async () => (await text("loginErr")).includes("hatalı")));
await type("pin", "1234");
check("Giris yapildi", await waitFor(() => visible("list")));
check("Lot listesi geldi", await waitFor(async () => (await text("lotList")).includes("LOT-3")));
await shot("h1_liste");

await ev(`[...document.querySelectorAll("#lotList li")].find(li => li.textContent.includes("LOT-3")).click()`);
await click("startBtn");
check("Okutma ekrani", await waitFor(() => visible("scan")));
check("Lot buyuk yazili", (await text("lotTitle")) === "LOT-3");

await type("scanBox", "K1000001");
check("Kabul yesil", await waitFor(async () => (await cls("band")) === "success"));
check("Sayac 1", await waitFor(async () => (await text("sCount")) === "1"));
check("Sunucuda ayrildi", (await state("K1000001")) === "reserved");
await shot("h2_kabul");

await type("scanBox", "K1000006");
check("Yanlis lot kirmizi", await waitFor(async () => (await cls("band")) === "error"));
check("Yanlis lot mesaji", (await text("bandD")).includes("Seçili lot: LOT-3"));
await shot("h3_ret");

await type("scanBox", "K1000002");
await type("scanBox", "K1000002");   // okuyucunun çift okuması
await waitFor(async () => (await text("sCount")) === "2");
await sleep(800);
const d = await (await fetch(server + "api/v1/rolls/K1000002")).json();
check("Cift okuma tek kayit", d.movements.filter(m => m.kind === "reserved").length === 1);

await click("remBtn");
await type("scanBox", "K1000002");
check("Cikarma modu stoga dondurdu", await waitFor(async () => (await state("K1000002")) === "in_stock"));
await click("remBtn");

// Ağ kesildi: okutma kuyrukta bekler, ağ gelince bir kez gider
await send("Network.emulateNetworkConditions", { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
await type("scanBox", "K1000004");
check("Ag yok uyarisi", await waitFor(async () => (await cls("band")) === "offline", 15000));
await shot("h4_baglanti_yok");
await send("Network.emulateNetworkConditions", { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
check("Ag gelince gonderildi", await waitFor(async () => (await state("K1000004")) === "reserved", 15000));
const d4 = await (await fetch(server + "api/v1/rolls/K1000004")).json();
check("Kuyruktaki okutma tek kez islendi", d4.movements.filter(m => m.kind === "reserved").length === 1);

await click("confirmBtn");
check("Onay paneli", await visible("confirmPanel"));
await click("confirmYes");
check("Onaylandi, listeye dondu", await waitFor(async () => (await text("listMsg")).includes("onaylandı")));
check("Sunucuda sevk edildi", (await state("K1000001")) === "shipped" && (await state("K1000004")) === "shipped");
await shot("h5_onay");

await ev(`[...document.querySelectorAll("#lotList li")].find(li => li.textContent.includes("LOT-3")).click()`);
await click("startBtn");
await waitFor(() => visible("scan"));
await type("scanBox", "K1000005");
await waitFor(async () => (await state("K1000005")) === "reserved");
await click("cancelBtn");
await click("cancelYes");
check("Sebepsiz iptal olmadi", (await text("cancelErr")).includes("sebep"));
await ev(`document.getElementById("cancelReason").selectedIndex = 1`);
await click("cancelYes");
check("Iptal edildi", await waitFor(async () => (await text("listMsg")).includes("iptal edildi")));
check("Iptalde stoga dondu", (await state("K1000005")) === "in_stock");

await click("logout");
check("Cikis", await waitFor(() => visible("login")));

ws.close();
console.log(results.join("\n"));
process.exit(0);
