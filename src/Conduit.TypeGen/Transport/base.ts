export interface DispatchResult<T> {
  /** The handler's result: null when the handler answers null (a `T?` result), and on a refusal. Always written. */
  data?: T | null;
  /** The server's refusal. Absent on success: the server leaves an absent member off rather than sending null. */
  error?: ConduitError;
}

export interface ConduitError {
  code: string;
  message: string;
  /** Absent when the refusal carries none. */
  details?: ConduitErrorDetail[];
  /** The error's HTTP-status category. 0 from a server that predates it. */
  status: number;
  /** A declared refusal's full type name; narrow with `isError`. Absent otherwise. */
  type?: string;
  data?: unknown;
  /** An unexpected failure's support reference. Absent on a refusal. */
  reference?: string;
}

export interface ConduitErrorDetail {
  /** The field the detail is about. Absent for an object-level detail. */
  field?: string;
  message: string;
}

/**
 * How a client reaches a Conduit host. A server's refusal is a result (`error`); a transport failure rejects.
 * `headers` are the dispatch's properties: the server reads them into `MessageContext.Properties`.
 */
export interface ConduitTransport {
  sendRequest<TResult>(type: string, payload: unknown, headers?: Record<string, string>): Promise<DispatchResult<TResult>>;
  sendEvent(type: string, data: unknown, headers?: Record<string, string>): Promise<void>;
  subscribe(eventName: string, handler: (data: unknown) => void): Subscription;
}

export interface Subscription {
  unsubscribe(): void;
}
