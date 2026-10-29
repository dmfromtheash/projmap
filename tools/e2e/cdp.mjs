// Node 22 built-ins only. Each CDP request is independently timed and cancellable.
export class CDP {
  constructor(ws) {
    this.ws = ws; this.seq = 0; this.pending = new Map(); this.listeners = new Map();
    ws.addEventListener('message', e => {
      const m = JSON.parse(e.data);
      if (m.id) {
        const p = this.pending.get(m.id); if (!p) return;
        this.pending.delete(m.id); clearTimeout(p.timer);
        m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result);
      } else for (const fn of this.listeners.get(m.method) || []) fn(m.params);
    });
    ws.addEventListener('close', () => this.fail(new Error('CDP disconnected')));
    ws.addEventListener('error', () => this.fail(new Error('CDP WebSocket error')));
  }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((resolve, reject) => {
      ws.addEventListener('open', resolve, { once: true });
      ws.addEventListener('error', reject, { once: true });
      setTimeout(() => reject(new Error('CDP connect timeout')), 10000).unref();
    });
    return new CDP(ws);
  }
  on(name, fn) { if (!this.listeners.has(name)) this.listeners.set(name, []); this.listeners.get(name).push(fn); }
  send(method, params = {}, timeout = 45000) {
    return new Promise((resolve, reject) => {
      const id = ++this.seq;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`${method} timeout ${timeout} ms`)); }, timeout);
      this.pending.set(id, { resolve, reject, timer, sentAt: performance.now(), method });
      try { this.ws.send(JSON.stringify({ id, method, params })); }
      catch (e) { clearTimeout(timer); this.pending.delete(id); reject(e); }
    });
  }
  async evaluate(expression, timeout = 45000) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, timeout);
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    return r.result.value;
  }
  fail(error) { for (const p of this.pending.values()) { clearTimeout(p.timer); p.reject(error); } this.pending.clear(); }
  close() { this.fail(new Error('CDP closed by harness')); this.ws.close(); }
}
