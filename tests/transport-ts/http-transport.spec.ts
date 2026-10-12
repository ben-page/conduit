import { afterEach, describe, expect, it, vi } from 'vitest';
import { HttpTransport } from '../../src/Conduit.TypeGen/Transport/http';

/** The slice of a fetch Response that HttpTransport reads. */
interface FakeResponse {
  ok: boolean;
  status: number;
  text(): Promise<string>;
  json(): Promise<unknown>;
}

function respond(status: number, body: string): FakeResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(body),
    json: () => Promise.resolve(JSON.parse(body) as unknown),
  };
}

function stubFetch(response: FakeResponse) {
  const fetchSpy = vi.fn((_url: string, _init: RequestInit) => Promise.resolve(response));
  vi.stubGlobal('fetch', fetchSpy);
  return fetchSpy;
}

describe('HttpTransport', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('posts a request to <baseUrl>/api/conduit/dispatch by default, named by X-Conduit-Type', async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":42}'));

    const result = await new HttpTransport('https://host').sendRequest<number>('App.Ping', { a: 1 });

    expect(result).toEqual({ data: 42 });
    expect(fetchSpy).toHaveBeenCalledWith('https://host/api/conduit/dispatch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Conduit-Type': 'App.Ping' },
      body: '{"a":1}',
    });
  });

  it("uses the base path it is given, and sends the caller's headers too", async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));

    await new HttpTransport('', '/svc').sendRequest('App.Ping', {}, { 'X-Tenant': 't1' });

    expect(fetchSpy).toHaveBeenCalledWith('/svc/dispatch', expect.objectContaining({
      headers: { 'Content-Type': 'application/json', 'X-Conduit-Type': 'App.Ping', 'X-Tenant': 't1' },
    }));
  });

  it('returns the error a refused request carries instead of throwing', async () => {
    stubFetch(respond(400, '{"error":{"code":"Validation","message":"Name is required.","status":400}}'));

    const result = await new HttpTransport('').sendRequest('App.Create', {});

    expect(result.error).toEqual({ code: 'Validation', message: 'Name is required.', status: 400 });
  });

  it('throws, naming the status, when a failed response is not JSON', async () => {
    stubFetch(respond(502, 'Bad Gateway'));

    await expect(new HttpTransport('').sendRequest('App.Ping', {})).rejects.toThrow(
      'Conduit dispatch failed with status 502: Bad Gateway');
  });

  it('posts an event to /publish, and throws when the server refuses it', async () => {
    const fetchSpy = stubFetch(respond(204, ''));
    await new HttpTransport('https://host').sendEvent('App.Happened', { id: 7 });
    expect(fetchSpy).toHaveBeenCalledWith('https://host/api/conduit/publish', expect.objectContaining({ body: '{"id":7}' }));

    stubFetch(respond(500, ''));
    await expect(new HttpTransport('').sendEvent('App.Happened', {})).rejects.toThrow(
      'Conduit publish failed with status 500');
  });

  it('refuses a subscription, because HTTP has no server push', () => {
    expect(() => new HttpTransport('').subscribe('App.Happened', () => undefined)).toThrow(
      'HttpTransport does not support event subscriptions');
  });
});
