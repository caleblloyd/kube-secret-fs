#!/bin/sh -e

cd $(dirname $0)

build="false"
logs="false"
script="$0"

docker_compose() {
    docker-compose -f cicd/sdk/docker-compose.yaml -p ksfs "$@"
}

wait_secret() {
    ready="0"
    for i in $(seq 1 $1); do
        ready=$(docker exec ksfs-k3d \
            kubectl -n "$2" get secret -o name | grep "$3" | wc -l)
        
        if [ "$ready" != "0" ]; then
            break
        fi
        echo "waiting for ns:${2} secret:${3} to be ready: ${i}s"
        sleep 1
    done
    if [ "$ready" = "0" ]; then
        echo "ns:${2} secret:${3} not ready after ${1} seconds" >&2
        exit 1
    fi
}

wait_deployment() {
    ready="0"
    for i in $(seq 1 $1); do
        ready=$(docker exec ksfs-k3d \
            kubectl -n "$2" get deployment "$3" -o 'jsonpath={.status.readyReplicas}')
        ready="${ready:-0}"
        if [ "$ready" != "0" ]; then
            break
        fi
        echo "waiting for ns:${2} deployment:${3} to be ready: ${i}s"
        sleep 1
    done
    if [ "$ready" = "0" ]; then
        echo "ns:${2} deployment:${3} not ready after ${1} seconds" >&2
        echo "if you are on a slow network, consider importing images from docker:" >&2
        echo "" >&2
        echo "   $script up --import-images" >&2
        echo "" >&2
        exit 1
    fi
}

up_usage() {
cat >&2 <<EOF
start k3d

    usage: $script up <options>

options:
        --build          - build new container
        --logs           - tail logs on dotnet container
    -h, --help           - print this message

EOF
}

up() {
    while test $# -gt 0
    do
        case "$1" in
            -h | --help)
                up_usage
                exit 0
                ;;
            --build)
                build="true"
                ;;
            --logs)
                logs="true"
                ;;
            *)
                up_usage
                exit 1
                ;;
        esac
        shift
    done

    # ensure compose.resolv.conf exists
    if ! [ -f cicd/sdk/k3d.resolv.conf ]; then
        cp cicd/sdk/k3d.resolv.conf.example cicd/sdk/k3d.resolv.conf
    fi

    # start docker-compose
    if ! docker inspect ksfs-k3d >/dev/null 2>&1; then
        build="true"
    fi
    if [ "$build" = "true" ]; then
        echo "cleaning old docker-compose stack if one exists" >&2
        docker_compose down -v

        echo "building docker-compose stack" >&2
        echo "" >&2
        docker_compose up -d -V --build
    else
        echo "starting existing docker-compose stack" >&2
        echo "to recreate with updated containers, run:" >&2
        echo "" >&2
        echo "   $script up --build" >&2
        echo "" >&2
        echo "" >&2
        docker_compose up -d
    fi

    # set first-run status
    first_run_do="true"
    if docker exec ksfs-k3d sh -c '[ -f "/etc/first-run-complete" ]'; then
        first_run_do="false"
    fi

    # wait for default namespace to be ready
    echo "" >&2
    echo "waiting for default namespace to be ready" >&2
    wait_secret "120" "default" "default-token"

    # actions on first-run
    if [ "$first_run_do" = "true" ]; then
        echo "" >&2
        echo "performing first-run actions" >&2

        # copy service account token on first-run
        echo "" >&2
        echo "copying service account token" >&2
        docker exec ksfs-k3d sh -c '
                cd /var/run/secrets/kubernetes.io/serviceaccount
                secret=$(kubectl get secret \
                        -o name  \
                    | grep "default-token")
                kubectl get "$secret" \
                    -o "jsonpath={.data.ca\.crt}" \
                    | base64 -d \
                    > ca.crt
                kubectl get "$secret" \
                    -o "jsonpath={.data.namespace}" \
                    | base64 -d \
                    > namespace
                kubectl get "$secret" \
                    -o "jsonpath={.data.token}" \
                    | base64 -d \
                    > token
            '

        # wait for coredns to be ready
        echo "" >&2
        echo "waiting for coredns to be ready" >&2
        wait_deployment "300" "kube-system" "coredns"
        
        # apply seed data
        echo "" >&2
        echo "applying seed resources" >&2
        docker exec ksfs-k3d \
            kubectl apply -f /seed/
            
        # sleep to allow rbac rules to update
        sleep 2       

        echo "" >&2
        echo "first-run actions complete" >&2
        docker exec ksfs-k3d touch "/etc/first-run-complete"
    fi

    echo "" >&2
    echo "docker-compose stack has started" >&2
    echo "" >&2

    if [ "$logs" = "true" ]; then
      docker_compose logs -f dotnet
    fi
}

stop() {
    docker_compose stop
}

destroy() {
    docker_compose down -v
}

get_kubeconfig() {
    docker exec -it ksfs-k3d sh -c '
        kubectl config view --raw \
            | sed "s/127\.0\.0\.1:6443/localhost:${K3D_PORT}/g"
    '
}

shell() {
    docker exec -it ksfs-k3d sh
}

usage() {
cat >&2 <<-EOF
manage local HobbyFarm development environment

        usage: $script <options> <command>
        
where <command> is one of:

    up          - create or start k3d
    stop        - stop k3d
    destroy     - destroy k3d
    shell       - drop a shell into k3d container

options:
    -h, --help  - print this message

EOF
}

case "$1" in
    -h | --help)
        usage
        exit 0
        ;;
    up)
        shift
        up "$@"
        ;;
    stop)
        stop
        ;;
    destroy)
        destroy
        ;;
    kubeconfig)
        get_kubeconfig
        ;;
    shell)
        shell
        ;;
    *)
        usage
        exit 1
        ;;
esac
