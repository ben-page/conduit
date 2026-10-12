// Ported from ManagementSystem's src/ManagementSystem.Web/Client/src/app/core/conduit/signalr-transport.spec.ts
// (0fd5a045ab), which keeps its own copy against its committed conduit.ts. Only the transport import changed: this
// suite runs the shipped source, src/Conduit.TypeGen/Transport/signalr.ts.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HubConnectionBuilder, HubConnectionState, type IRetryPolicy } from '@microsoft/signalr';
import {
  DEFAULT_RETRY_POLICY,
  SignalRTransport,
  type TransportConnectionState,
} from '../../src/Conduit.TypeGen/Transport/signalr';

/**
 * A hand-rolled HubConnection. The tests set `state` and fire the lifecycle callbacks themselves, because the
 * point is to reach states the real client reaches only after minutes offline — which no test should wait for.
 */
class FakeHubConnection {
  public state: HubConnectionState = HubConnectionState.Disconnected;
  public startCalls = 0;
  /** Set to make the next start() reject, once. */
  public startFailure: Error | null = null;
  /** Set to make every start() reject until it is cleared — the server staying down. */
  public failEveryStart: Error | null = null;
  private readonly handlers = {
    reconnecting: [] as (() => void)[],
    reconnected: [] as (() => void)[],
    close: [] as (() => void)[],
  };

  public start(): Promise<void> {
    this.startCalls += 1;
    const failure = this.failEveryStart ?? this.startFailure;
    if (failure) {
      if (!this.failEveryStart) this.startFailure = null;
      return Promise.reject(failure);
    }
    this.state = HubConnectionState.Connected;
    return Promise.resolve();
  }

  // The real client fires onclose while stop() runs. The fake does the same, so the intentional-stop test
  // exercises the ordering the transport has to get right.
  public stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
    this.fire('close');
    return Promise.resolve();
  }

  public invoke(): Promise<unknown> {
    if (this.state !== HubConnectionState.Connected) {
      return Promise.reject(new Error("Cannot send data if the connection is not in the 'Connected' State."));
    }
    return Promise.resolve({ data: 'ok' });
  }

  public on(): void { /* these tests push no server messages, so there is nothing to subscribe to */ }

  public off(): void { /* nothing is ever subscribed */ }

  public onreconnecting(handler: () => void): void {
    this.handlers.reconnecting.push(handler);
  }

  public onreconnected(handler: () => void): void {
    this.handlers.reconnected.push(handler);
  }

  public onclose(handler: () => void): void {
    this.handlers.close.push(handler);
  }

  public fire(kind: 'reconnecting' | 'reconnected' | 'close'): void {
    this.handlers[kind].forEach((handler) => handler());
  }
}

function watch(transport: SignalRTransport): TransportConnectionState[] {
  const seen: TransportConnectionState[] = [];
  transport.onStateChange((state) => seen.push(state));
  return seen;
}

describe('SignalRTransport — a connection that died is started again (B1)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // THE regression (open:conduit-transport-wedge). connect() memoized a resolved start forever, so once the
  // connection closed for good every later dispatch threw "Cannot send data…" until the page reloaded.
  it('starts a connection again after it closed for good, instead of wedging every dispatch', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.sendRequest('Ping', {});
    expect(connection.startCalls).toBe(1);

    // The retry policy stopped, or the server closed the socket without allowing a reconnect.
    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');

    await expect(transport.sendRequest('Ping', {})).resolves.toEqual({ data: 'ok' });
    expect(connection.startCalls).toBe(2);
  });

  // The resilience property that already existed, kept: a FAILED start must not be cached either.
  it('retries after a failed start instead of caching the rejection', async () => {
    const connection = new FakeHubConnection();
    connection.startFailure = new Error('unreachable');
    const transport = new SignalRTransport(connection as never);

    await expect(transport.sendRequest('Ping', {})).rejects.toThrow('unreachable');
    await expect(transport.sendRequest('Ping', {})).resolves.toEqual({ data: 'ok' });
    expect(connection.startCalls).toBe(2);
  });

  // A live connection must not be restarted per dispatch — that would renegotiate constantly.
  it('reuses a live connection across dispatches', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);

    await transport.sendRequest('Ping', {});
    await transport.sendRequest('Ping', {});

    expect(connection.startCalls).toBe(1);
  });
});

describe('DEFAULT_RETRY_POLICY', () => {
  function delayAfter(previousRetryCount: number): number | null {
    return DEFAULT_RETRY_POLICY.nextRetryDelayInMilliseconds({
      previousRetryCount,
      elapsedMilliseconds: 0,
      retryReason: new Error('socket closed'),
    });
  }

  it('backs off 0 s, 2 s, 5 s, 10 s, 30 s', () => {
    expect([0, 1, 2, 3, 4].map(delayAfter)).toEqual([0, 2000, 5000, 10000, 30000]);
  });

  // SignalR stops reconnecting for good the first time a policy returns null. An unattended kiosk must not.
  it('never gives up, however long the outage', () => {
    expect([5, 50, 5000].map(delayAfter)).toEqual([30000, 30000, 30000]);
  });
});

describe('SignalRTransport — the retry policy the hub is built with', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  function spyOnBuilder() {
    // build() would construct a real HttpConnection; by then the policy has already been chosen.
    vi.spyOn(HubConnectionBuilder.prototype, 'build').mockReturnValue(new FakeHubConnection() as never);
    return vi.spyOn(HubConnectionBuilder.prototype, 'withAutomaticReconnect');
  }

  it('uses the unbounded default when no policy is supplied', () => {
    const withAutomaticReconnect = spyOnBuilder();
    new SignalRTransport('/hub');
    expect(withAutomaticReconnect).toHaveBeenCalledWith(DEFAULT_RETRY_POLICY);
  });

  it('uses a supplied policy instead of the default', () => {
    const withAutomaticReconnect = spyOnBuilder();
    const policy: IRetryPolicy = { nextRetryDelayInMilliseconds: () => null };
    new SignalRTransport('/hub', { retryPolicy: policy });
    expect(withAutomaticReconnect).toHaveBeenCalledWith(policy);
  });

  it('builds no reconnect at all when automaticReconnect is false', () => {
    const withAutomaticReconnect = spyOnBuilder();
    new SignalRTransport('/hub', { automaticReconnect: false });
    expect(withAutomaticReconnect).not.toHaveBeenCalled();
  });
});

describe('SignalRTransport.onStateChange', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('reports a start, a drop and a recovery, in order', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);

    await transport.connect();
    connection.fire('reconnecting');
    connection.fire('reconnected');

    // The leading 'stopped' is the replay onStateChange gives every new subscriber.
    expect(seen).toEqual(['stopped', 'connecting', 'connected', 'reconnecting', 'connected']);
  });

  // A late subscriber must not show 'stopped' for a socket that is already up.
  it('replays the current state to a late subscriber', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.connect();

    expect(watch(transport)).toEqual(['connected']);
  });

  it('reports a close nobody asked for as reconnecting, not disconnected', async () => {
    vi.useFakeTimers();
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');

    expect(seen.at(-1)).toBe('reconnecting');
    expect(seen).not.toContain('disconnected');
  });

  // Ruling 1 (2026-09-11): logout, re-login and device re-attest all stop the socket on purpose. That close is
  // never a loss, so it must never be reported as 'disconnected'.
  it('reports an intentional stop as stopped, never as disconnected', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);

    await transport.connect();
    await transport.stop();

    expect(seen).toEqual(['stopped', 'connecting', 'connected', 'stopped']);
  });

  it('retries a failed first start as connecting, never as disconnected', async () => {
    vi.useFakeTimers();
    const connection = new FakeHubConnection();
    connection.startFailure = new Error('unreachable');
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);

    await expect(transport.connect()).rejects.toThrow('unreachable');
    expect(seen).toEqual(['stopped', 'connecting']);

    // DEFAULT_RETRY_POLICY's first delay is 0 ms: it tries again with nobody touching the screen.
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);
    expect(seen).toEqual(['stopped', 'connecting', 'connected']);
  });

  it('stops notifying after unsubscribe', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen: TransportConnectionState[] = [];
    transport.onStateChange((state) => seen.push(state)).unsubscribe();

    await transport.connect();

    expect(seen).toEqual(['stopped']);
  });
});

describe('SignalRTransport — a close nobody asked for is retried, with backoff (4.4a)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // The ruling this suite exists for: a kiosk must come back on its own, without a badge scan.
  it('starts again after a server close, with nobody touching the screen', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);

    expect(connection.startCalls).toBe(2);
    expect(seen.at(-1)).toBe('connected');
  });

  it('backs off 0 s, 2 s, 5 s, 10 s while the server stays down', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.connect();
    connection.failEveryStart = new Error('server down');

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');

    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(1999);
    expect(connection.startCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(connection.startCalls).toBe(3);
    await vi.advanceTimersByTimeAsync(5000);
    expect(connection.startCalls).toBe(4);
    await vi.advanceTimersByTimeAsync(10000);
    expect(connection.startCalls).toBe(5);
  });

  // Otherwise a server that accepts a connection and drops it at once is hammered every 0 ms.
  it('keeps backing off when a restart connects and the server closes it straight away', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);

    // Up for a second, then closed again: the schedule continues at 2 s rather than restarting at 0 ms.
    await vi.advanceTimersByTimeAsync(1000);
    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(2000);
    expect(connection.startCalls).toBe(3);
  });

  it('starts the schedule over after a connection that stayed up 30 s', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);

    await vi.advanceTimersByTimeAsync(30000);
    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);

    expect(connection.startCalls).toBe(3);
  });

  // Logout, re-login and device re-attest stop the socket on purpose. A pending restart must not undo that.
  it('cancels a pending restart when the app stops the connection', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    const seen = watch(transport);
    await transport.connect();
    connection.failEveryStart = new Error('server down');

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await transport.stop();
    await vi.advanceTimersByTimeAsync(60000);

    expect(connection.startCalls).toBe(1);
    expect(seen.at(-1)).toBe('stopped');
  });

  it('connects at once when a dispatch arrives during a wait', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never);
    await transport.connect();
    connection.failEveryStart = new Error('server down');

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);

    connection.failEveryStart = null;
    await expect(transport.sendRequest('Ping', {})).resolves.toEqual({ data: 'ok' });
    expect(connection.startCalls).toBe(3);

    // And the wait it interrupted is really cancelled. If it were not, `restartTimer` would still be set, the next
    // close would hit scheduleRestart's "a restart is already pending" guard and be dropped, and the stale timer —
    // due 2 s after the close before it — would fire early instead of the schedule's next step.
    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(2000);
    expect(connection.startCalls).toBe(3);
    await vi.advanceTimersByTimeAsync(3000);
    expect(connection.startCalls).toBe(4);
  });

  // 'disconnected' is what an app that opted out still sees.
  it('reports disconnected, and never retries, when automaticReconnect is false', async () => {
    const connection = new FakeHubConnection();
    const transport = new SignalRTransport(connection as never, { automaticReconnect: false });
    const seen = watch(transport);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(60000);

    expect(seen.at(-1)).toBe('disconnected');
    expect(connection.startCalls).toBe(1);
  });

  it('reports disconnected when the supplied policy gives up', async () => {
    const connection = new FakeHubConnection();
    const policy: IRetryPolicy = { nextRetryDelayInMilliseconds: () => null };
    const transport = new SignalRTransport(connection as never, { retryPolicy: policy });
    const seen = watch(transport);
    await transport.connect();

    connection.state = HubConnectionState.Disconnected;
    connection.fire('close');
    await vi.advanceTimersByTimeAsync(60000);

    expect(seen.at(-1)).toBe('disconnected');
    expect(connection.startCalls).toBe(1);
  });
});
