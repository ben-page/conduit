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

  it('uses the base path it is given', async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));

    await new HttpTransport('', '/svc').sendRequest('App.Ping', {});

    expect(fetchSpy).toHaveBeenCalledWith('/svc/dispatch', expect.anything());
  });

  // The hub carries the same argument as properties (ConduitHub's `headers`), so it means one thing on both transports.
  it("sends the caller's headers as X-Conduit-Property-<name> headers, on a request and an event alike", async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));

    await new HttpTransport('').sendRequest('App.Ping', {}, { TenantCode: 't1', culture: 'es' });
    await new HttpTransport('').sendEvent('App.Happened', {}, { TenantCode: 't1', culture: 'es' });

    for (const [, init] of fetchSpy.mock.calls) {
      expect(init.headers).toEqual({
        'Content-Type': 'application/json',
        'X-Conduit-Type': expect.any(String),
        'X-Conduit-Property-TenantCode': 't1',
        'X-Conduit-Property-culture': 'es',
      });
    }
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  // A property is never a header of its own: it cannot replace the content type or the message type, nor set a
  // correlation id, which is the server's to mint.
  it("never lets a caller's header replace the content type, the type or a correlation id", async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));
    const headers = {
      'Content-Type': 'text/plain',
      'X-Conduit-Type': 'App.Other',
      'X-Conduit-Message-Id': 'm-1',
    };

    await new HttpTransport('').sendRequest('App.Ping', {}, headers);

    expect(fetchSpy.mock.calls[0]?.[1].headers).toEqual({
      'Content-Type': 'application/json',
      'X-Conduit-Type': 'App.Ping',
      'X-Conduit-Property-Content-Type': 'text/plain',
      'X-Conduit-Property-X-Conduit-Type': 'App.Other',
      'X-Conduit-Property-X-Conduit-Message-Id': 'm-1',
    });
  });

  it('returns the error a refused request carries instead of throwing', async () => {
    stubFetch(respond(400, '{"error":{"code":"Validation","message":"Name is required.","status":400}}'));

    const result = await new HttpTransport('').sendRequest('App.Create', {});

    expect(result.error).toEqual({ code: 'Validation', message: 'Name is required.', status: 400 });
  });

  it('throws, naming the status and the URL, when a failed response is not JSON', async () => {
    stubFetch(respond(502, 'Bad Gateway'));

    await expect(new HttpTransport('https://host').sendRequest('App.Ping', {})).rejects.toThrow(
      'Conduit: HTTP 502 from https://host/api/conduit/dispatch');
  });

  // The release-review finding: a 401/403 ProblemDetails was returned as a DispatchResult with neither data nor error,
  // and `if (!result.data) return;` did nothing at all. A failure that is not the server's refusal is not a result.
  it('throws when a failed response is JSON but not a DispatchResult, such as an authorization ProblemDetails', async () => {
    stubFetch(respond(401, '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Unauthorized","status":401}'));
    await expect(new HttpTransport('https://host').sendRequest('App.Ping', {})).rejects.toThrow(
      'Conduit: HTTP 401 from https://host/api/conduit/dispatch');

    stubFetch(respond(403, ''));
    await expect(new HttpTransport('').sendRequest('App.Ping', {})).rejects.toThrow('Conduit: HTTP 403 from /api/conduit/dispatch');

    // An OAuth error body has an `error` key, but it is not a ConduitError.
    stubFetch(respond(401, '{"error":"invalid_token","error_description":"The token expired."}'));
    await expect(new HttpTransport('').sendRequest('App.Ping', {})).rejects.toThrow('Conduit: HTTP 401 from /api/conduit/dispatch');
  });

  it('posts an event to /publish, and throws, naming the status and the URL, when it fails', async () => {
    const fetchSpy = stubFetch(respond(204, ''));
    await new HttpTransport('https://host').sendEvent('App.Happened', { id: 7 });
    expect(fetchSpy).toHaveBeenCalledWith('https://host/api/conduit/publish', expect.objectContaining({ body: '{"id":7}' }));

    stubFetch(respond(500, ''));
    await expect(new HttpTransport('').sendEvent('App.Happened', {})).rejects.toThrow(
      'Conduit: HTTP 500 from /api/conduit/publish');
  });

  // /publish answers a refused event with the DispatchResult /dispatch answers a refused request with. An event has no
  // result to carry it, so the publish rejects, with the refusal's message (as the hub's Publish does).
  it("rejects a refused event with the refusal's message", async () => {
    stubFetch(respond(403, '{"data":null,"error":{"code":"Forbidden","message":"Events of type App.Happened are not accepted.","status":403}}'));

    await expect(new HttpTransport('').sendEvent('App.Happened', {})).rejects.toThrow(
      'Events of type App.Happened are not accepted.');
  });

  it('sends the credentials mode it is given, on a request and an event alike', async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));
    const transport = new HttpTransport('https://api.example.com', undefined, { credentials: 'include' });

    await transport.sendRequest('App.Ping', {});
    await transport.sendEvent('App.Happened', {});

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    for (const [, init] of fetchSpy.mock.calls) {
      expect(init.credentials).toBe('include');
    }
  });

  it('hands every request the abort signal it is given', async () => {
    const fetchSpy = stubFetch(respond(200, '{"data":null}'));
    const controller = new AbortController();
    const transport = new HttpTransport('', undefined, { signal: controller.signal });

    await transport.sendRequest('App.Ping', {});
    await transport.sendEvent('App.Happened', {});

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    for (const [, init] of fetchSpy.mock.calls) {
      expect(init.signal).toBe(controller.signal);
    }
  });

  // The real fetch, no stub: an aborted signal stops the request before it reaches the network.
  it('rejects, without sending, once its signal is aborted', async () => {
    const transport = new HttpTransport('http://127.0.0.1:9', undefined, { signal: AbortSignal.abort() });

    await expect(transport.sendRequest('App.Ping', {})).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('sends through the fetch it is given instead of the global one, called unbound as the global fetch must be', async () => {
    const globalFetch = stubFetch(respond(200, '{"data":1}'));
    const receivers: unknown[] = [];
    const hook = vi.fn(function (this: unknown, _url: string, _init: RequestInit) {
      receivers.push(this);
      return Promise.resolve(respond(200, '{"data":2}'));
    });
    const transport = new HttpTransport('', undefined, { fetch: hook as unknown as typeof fetch });

    const result = await transport.sendRequest<number>('App.Ping', {});

    expect(result).toEqual({ data: 2 });
    expect(hook).toHaveBeenCalledWith('/api/conduit/dispatch', expect.objectContaining({ method: 'POST' }));
    expect(globalFetch).not.toHaveBeenCalled();
    // A window's fetch throws "Illegal invocation" when it is called as a method of another object.
    expect(receivers).toEqual([undefined]);
  });

  it('refuses a subscription, because HTTP has no server push', () => {
    expect(() => new HttpTransport('').subscribe('App.Happened', () => undefined)).toThrow(
      'HttpTransport does not support event subscriptions');
  });
});
