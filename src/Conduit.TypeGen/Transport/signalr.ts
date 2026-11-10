import { HubConnectionBuilder, HubConnection, HubConnectionState } from '@microsoft/signalr';
import type { IRetryPolicy, RetryContext } from '@microsoft/signalr';
import type { ConduitTransport, DispatchResult, Subscription } from './base';

/**
 * The reconnect schedule a SignalRTransport uses unless its options supply another: 0 s, 2 s,
 * 5 s, 10 s, then every 30 s for as long as the outage lasts.
 *
 * Unbounded on purpose. SignalR's stock withAutomaticReconnect() schedule is [0, 2 s, 10 s, 30 s],
 * and then it stops for good. That suits a desktop tab and bricks an unattended screen: a wall-mounted
 * display that loses its network for a minute has nobody standing there to reload it.
 */
export const DEFAULT_RETRY_POLICY: IRetryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: RetryContext): number {
    const schedule = [0, 2000, 5000, 10000, 30000];
    return schedule[Math.min(retryContext.previousRetryCount, schedule.length - 1)] ?? 30000;
  },
};

/**
 * The socket's lifecycle, as a consumer presents it.
 *
 * - stopped: nothing has started it yet, or the app called stop(). Never a loss.
 * - connecting: a start is in flight.
 * - connected: the socket is up.
 * - reconnecting: the socket dropped and the retry policy is working on it.
 * - disconnected: the socket closed and nothing is retrying. Only a transport built with
 *   automaticReconnect: false, or one whose retry policy gave up, reaches it; otherwise a close
 *   nobody asked for is followed by restarts on the policy's schedule -- reported as reconnecting
 *   once the socket has been up, and as connecting before it ever was.
 */
export type TransportConnectionState = 'stopped' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** What a server tells each client about itself as it connects (ConduitHub.OnConnectedAsync). */
export interface ConduitServerInfo {
  /** The host's build id. Opaque to Conduit. */
  buildId: string;
}

export interface SignalRTransportOptions {
  accessTokenFactory?: () => string | Promise<string>;
  automaticReconnect?: boolean;
  /** Replaces DEFAULT_RETRY_POLICY. Ignored when automaticReconnect is false. */
  retryPolicy?: IRetryPolicy;
  /**
   * How long, in milliseconds, a dispatch made while the hub is reconnecting waits for it to reconnect or close before
   * it rejects. Default 10 000.
   */
  reconnectWait?: number;
  configure?: (builder: HubConnectionBuilder) => void;
}

export class SignalRTransport implements ConduitTransport {
  private connection: HubConnection;
  private started?: Promise<void>;
  private connectionState: TransportConnectionState = 'stopped';
  private readonly connectionStateHandlers = new Set<(state: TransportConnectionState) => void>();
  private readonly restartPolicy: IRetryPolicy;
  private readonly restartsEnabled: boolean;
  private restartTimer?: ReturnType<typeof setTimeout>;
  private restartAttempt = 0;
  private restartStartedAt = 0;
  private connectedAt = 0;
  private hasConnected = false;
  private readonly reconnectWait: number;
  /** Dispatches waiting out a reconnect, released by the hub's next onreconnected or onclose. */
  private readonly reconnectWaiters = new Set<() => void>();

  /** A connection up this long is healthy: the next close starts the backoff schedule over. */
  private static readonly STABLE_CONNECTION_MS = 30000;

  constructor(pathOrConnection: string | HubConnection = '/conduit', options?: SignalRTransportOptions) {
    if (typeof pathOrConnection === 'string') {
      const builder = new HubConnectionBuilder()
        .withUrl(pathOrConnection, { accessTokenFactory: options?.accessTokenFactory });
      if (options?.automaticReconnect !== false) builder.withAutomaticReconnect(options?.retryPolicy ?? DEFAULT_RETRY_POLICY);
      options?.configure?.(builder);
      this.connection = builder.build();
    } else {
      this.connection = pathOrConnection;
    }
    this.restartPolicy = options?.retryPolicy ?? DEFAULT_RETRY_POLICY;
    this.restartsEnabled = options?.automaticReconnect !== false;
    this.reconnectWait = options?.reconnectWait ?? 10_000;
    this.connection.onreconnecting(() => this.setConnectionState('reconnecting'));
    this.connection.onreconnected(() => {
      this.reportConnected();
      this.releaseReconnectWaiters();
    });
    // stop() reports 'stopped' before it closes the socket, so the close it causes is not a loss.
    this.connection.onclose(error => {
      this.releaseReconnectWaiters();
      if (this.connectionState === 'stopped') return;
      this.scheduleRestart(error);
    });
  }

  /**
   * Starts the hub on first use (or call eagerly after login to warm it), and starts it again after it died.
   *
   * A failed start is not cached, so the next dispatch retries. A resolved start is not trusted either:
   * once the connection is Disconnected (the retry policy stopped, or the server closed it without
   * allowing a reconnect) a memoized promise would stay resolved while every invoke() threw, so the
   * live state decides. A dispatch during a restart wait connects at once: the pending timer is
   * cancelled here, and a failure schedules the next attempt.
   */
  connect(): Promise<void> {
    this.cancelRestart();
    if (this.started && this.connection.state === HubConnectionState.Disconnected) {
      this.started = undefined;
    }
    if (!this.started) {
      if (this.connectionState !== 'reconnecting') this.setConnectionState('connecting');
      this.started = this.connection.start()
        .then(() => { this.reportConnected(); })
        .catch(e => {
          this.started = undefined;
          if (this.connectionState !== 'stopped') this.scheduleRestart(e instanceof Error ? e : undefined);
          throw e;
        });
    }
    return this.started;
  }

  /** Stops the hub on purpose. Reports 'stopped' first, so the close this causes is never reported as a loss. */
  async stop(): Promise<void> {
    this.cancelRestart();
    this.started = undefined;
    this.restartAttempt = 0;
    this.connectedAt = 0;
    this.setConnectionState('stopped');
    await this.connection.stop();
  }

  /**
   * Subscribes to the socket's lifecycle. The handler is called at once with the current state, so a late
   * subscriber never shows 'stopped' for a socket that is already up. Deliberately not on ConduitTransport:
   * HttpTransport has no connection to report on.
   */
  onStateChange(handler: (state: TransportConnectionState) => void): Subscription {
    this.connectionStateHandlers.add(handler);
    handler(this.connectionState);
    return { unsubscribe: () => { this.connectionStateHandlers.delete(handler); } };
  }

  /**
   * Registers a handler for the server's identity, which ConduitHub sends each client as it connects and again on
   * every reconnect. Does NOT start the connection, so a consumer can register it before anyone has signed in and
   * still hear the first connect. Deliberately not on ConduitTransport: HttpTransport has no connection.
   */
  onServerInfo(handler: (info: ConduitServerInfo) => void): Subscription {
    this.connection.on('ConduitServerInfo', handler);
    return { unsubscribe: () => this.connection.off('ConduitServerInfo', handler) };
  }

  async sendRequest<TResult>(type: string, payload: unknown, headers?: Record<string, string>): Promise<DispatchResult<TResult>> {
    await this.ready();
    return await this.connection.invoke<DispatchResult<TResult>>('Dispatch', type, payload, null, headers ?? null);
  }

  async sendEvent(type: string, data: unknown, headers?: Record<string, string>): Promise<void> {
    await this.ready();
    await this.connection.invoke('Publish', type, data, null, headers ?? null);
  }

  subscribe(eventName: string, handler: (data: unknown) => void): Subscription {
    this.connection.on(eventName, handler);
    void this.connect().catch(() => { /* the failure scheduled the next attempt */ });
    return { unsubscribe: () => this.connection.off(eventName, handler) };
  }

  /**
   * Connects. While the hub is reconnecting, first waits (up to reconnectWait) for it to reconnect or close: invoke()
   * would reject "Cannot send data…" at once. After a close the connection is started again, unless the app stopped
   * it: a dispatch never undoes a stop it was waiting through. The state is read before anything is awaited, so the
   * waiter is registered before any transition can pass it by.
   */
  private async ready(): Promise<void> {
    if (this.connection.state === HubConnectionState.Reconnecting) {
      await this.waitForReconnect();
      if (this.connectionState === 'stopped') {
        throw new Error('Conduit: the connection was stopped while the dispatch waited for it to reconnect.');
      }
    }
    await this.connect();
  }

  private waitForReconnect(): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const release = () => {
        clearTimeout(timer);
        this.reconnectWaiters.delete(release);
        resolve();
      };
      const timer = setTimeout(() => {
        this.reconnectWaiters.delete(release);
        reject(new Error(`Conduit: the connection did not reconnect within ${this.reconnectWait} ms, so the dispatch was not sent.`));
      }, this.reconnectWait);
      this.reconnectWaiters.add(release);
    });
  }

  private releaseReconnectWaiters(): void {
    [...this.reconnectWaiters].forEach(release => release());
  }

  private reportConnected(): void {
    this.hasConnected = true;
    this.connectedAt = Date.now();
    this.setConnectionState('connected');
  }

  /**
   * Keeps trying after a close the app did not ask for, and after a start that failed -- the paths
   * withAutomaticReconnect does not cover. Waits come from the same retry policy the hub reconnects
   * with, so one schedule and one option cover both. The attempt count resets only after a connection
   * stayed up STABLE_CONNECTION_MS, so a server that accepts a connection and closes it at once is
   * retried on the schedule rather than in a tight loop.
   */
  private scheduleRestart(error?: Error): void {
    if (this.restartTimer !== undefined) return;
    if (!this.restartsEnabled) { this.setConnectionState('disconnected'); return; }
    if (this.connectedAt !== 0 && Date.now() - this.connectedAt >= SignalRTransport.STABLE_CONNECTION_MS) {
      this.restartAttempt = 0;
    }
    this.connectedAt = 0;
    if (this.restartAttempt === 0) this.restartStartedAt = Date.now();
    const delay = this.restartPolicy.nextRetryDelayInMilliseconds({
      previousRetryCount: this.restartAttempt,
      elapsedMilliseconds: Date.now() - this.restartStartedAt,
      retryReason: error ?? new Error('The connection closed without allowing a reconnect.'),
    });
    if (delay === null) { this.setConnectionState('disconnected'); return; }
    this.restartAttempt += 1;
    this.setConnectionState(this.hasConnected ? 'reconnecting' : 'connecting');
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined;
      void this.connect().catch(() => { /* the failure scheduled the next attempt */ });
    }, delay);
  }

  private cancelRestart(): void {
    if (this.restartTimer === undefined) return;
    clearTimeout(this.restartTimer);
    this.restartTimer = undefined;
  }

  private setConnectionState(state: TransportConnectionState): void {
    if (this.connectionState === state) return;
    this.connectionState = state;
    this.connectionStateHandlers.forEach(handler => handler(state));
  }
}
