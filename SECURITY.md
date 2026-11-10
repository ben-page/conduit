# Security policy

## Supported versions

Conduit is in preview. Security fixes are made in the latest preview release only, so upgrade to it to receive them.

| Version | Supported |
|---|---|
| Latest 1.0.0 preview | Yes |
| Earlier previews | No |

## Reporting a vulnerability

Do not report a vulnerability in a public issue, discussion or pull request.

Report it privately through GitHub: on the [repository](https://github.com/ben-page/conduit), open the **Security**
tab and choose **Report a vulnerability**. Include what you can of:

- the affected packages and versions;
- the transport, feature and configuration involved;
- the steps to reproduce it, or a proof of concept;
- the impact you expect.

You will receive an acknowledgement within five business days, and updates as the report is investigated. Once a fix
is ready, we agree a disclosure date with you and publish a GitHub security advisory, crediting you unless you prefer
otherwise. Please keep the details private until then.

## Securing an application

This page is the disclosure policy. For how to secure a Conduit application (the type filter, authorization on the
HTTP and SignalR edges, trusting caller-supplied ids), see [docs/security.md](docs/security.md).
