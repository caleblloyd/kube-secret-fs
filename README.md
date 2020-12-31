# kube-secret-fs

FUSE Filesystem backed to Kubernetes Secrets

## Purpose

Small Kubernetes utility pods need reliable persistent storage.  One popular model is to store Kubernetes utility data in CRDs, but CRDs have the following downside:

- CRDs are cluster-scoped and require cluster-level authorizations to install
- Applications must be developed assuming CRDs as the data storage model

`kube-secret-fs` simplifies things by allowing pods writeable access to Kubernetes secrets using the filesystem.  It currently requires `CAP_SYS_ADMIN` or `privileged` privileges.

## Use Cases

kube-secret-fs is intended to be used by a **single pod** and only in **very low-write workloads**.  Examples of good use cases:

- Status page that polls an endpoint every 10s and only writes to an Sqlite database if the endpoint status changes
- CronJob that needs to store state between runs

## Examples

- [NGINX Stateful Set](examples/stateful-set.yaml)
- [RBAC](examples/rbac.yaml)
