export interface DispatchResult<T> {
  /** The handler's result. Null (or absent) on a refusal. */
  data?: T | null;
  /** The server's refusal. Null (or absent) on success. */
  error?: ConduitError | null;
}

export interface ConduitError {
  code: string;
  message: string;
  details?: ConduitErrorDetail[] | null;
  /** The error's HTTP-status category. 0 from a server that predates it. */
  status: number;
  /** A declared refusal's full type name; narrow with `isError`. Null (or absent) otherwise. */
  type?: string | null;
  data?: unknown;
  /** An unexpected failure's support reference. Null (or absent) on a refusal and from older servers. */
  reference?: string | null;
}

export interface ConduitErrorDetail {
  /** The field the detail is about. Null (or absent) for an object-level detail. */
  field?: string | null;
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
