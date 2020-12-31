using System;
using k8s;

namespace KubeSecretFS
{
    public class KubeAccessor
    {
        private readonly Lazy<Kubernetes> _lazyClient =
            new(() => new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));

        public Kubernetes Client => _lazyClient.Value;
    }
}