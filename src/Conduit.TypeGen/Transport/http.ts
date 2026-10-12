import type { ConduitTransport, DispatchResult, Subscription } from './base';

export class HttpTransport implements ConduitTransport {
  constructor(private baseUrl: string, private basePath: string = '/api/conduit') {}

  async sendRequest<TResult>(type: string, payload: unknown, headers?: Record<string, string>): Promise<DispatchResult<TResult>> {
    const response = await fetch(this.baseUrl + this.basePath + '/dispatch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Conduit-Type': type, ...(headers ?? {}) },
      body: JSON.stringify(payload),
    });
    if (!response.ok) {
      const text = await response.text();
      try { return JSON.parse(text) as DispatchResult<TResult>; }
      catch { throw new Error(`Conduit dispatch failed with status ${response.status}: ${text}`); }
    }
    return await response.json() as DispatchResult<TResult>;
  }

  async sendEvent(type: string, data: unknown, headers?: Record<string, string>): Promise<void> {
    const response = await fetch(this.baseUrl + this.basePath + '/publish', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Conduit-Type': type, ...(headers ?? {}) },
      body: JSON.stringify(data),
    });
    if (!response.ok) {
      throw new Error(`Conduit publish failed with status ${response.status}`);
    }
  }

  subscribe(_eventName: string, _handler: (data: unknown) => void): Subscription {
    throw new Error('HttpTransport does not support event subscriptions. Use SignalRTransport for server-pushed events.');
  }
}
