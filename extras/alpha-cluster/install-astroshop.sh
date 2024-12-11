#!/bin/bash

# Instructions to get the AstroShop in the ACE Box running

# git clone ssh://git@bitbucket.lab.dynatrace.org/cloude/opentelemetry-demo-additions.git 
# cd opentelemetry-demo-additions/opentelemetry-demo-k8s

## 1 - INSTALL OPERATOR
helm upgrade --install dynatrace-operator oci://public.ecr.aws/dynatrace/dynatrace-operator \
--create-namespace \
--namespace dynatrace \
--atomic \
--version 1.3.2

kubectl -n dynatrace create secret generic [DYNAKUBE_NAME] --from-literal="apiToken=<DT_API_TOKEN>" --from-literal="dataIngestToken=<DT_INGEST_TOKEN>"

kubectl apply -f dynakube-oteldemo.yaml --namespace dynatrace


# INSTALL OPENTEL-OPERATOR

#Generate Dynatrace Access Tokens
#Dynatrace API token (DT_API_TOKEN)
#openTelemetryTrace.ingest
#metrics.ingest
#logs.ingest

## 2 - INOSTALL Cert-Manager
helm repo add jetstack https://charts.jetstack.io --force-update

helm install cert-manager jetstack/cert-manager --namespace cert-manager \
--create-namespace \
--version v1.15.3 \
--set crds.enabled=true

helm repo add open-telemetry https://open-telemetry.github.io/opentelemetry-helm-charts
helm repo update

helm install opentelemetry-operator open-telemetry/opentelemetry-operator --set "manager.collectorImage.repository=hcr.io/dynatrace/dynatrace-otel-collector/dynatrace-otel-collector:0.14.0" --create-namespace --namespace opentelemetry-operator-system --version 0.67.0

kubectl create secret generic dynatrace-otelcol-dt-api-credentials --from-literal=DT_ENDPOINT=https://sro97894.live.dynatrace.com/api/v2/otlp --from-literal=DT_API_TOKEN=dt0c01.VH7PNJNMB6K2Q3MTLLYCRGQ7.xxx --namespace staging-astroshop
# do for production-astroshop, staging-astroshop,
kubectl apply -f otel_collector_gateway.yaml --namespace astroshop
# do for production-astroshop, staging-astroshop, 

kubectl apply -f otel_k8s_enrichment.yaml
kubectl apply -f otel_k8s_enrichment-staging.yaml
kubectl apply -f otel_k8s_enrichment-production.yaml

# Install OtelDemo!!
# run Ansible on ACE BOX
ace enable https://github.com/dynatrace-ace/perform-2025-hot-dynatrace-for-developers.git --local
# Modify main.yaml to just run what we want
# find otel

# GET Kubectl  YAML files
# TODO: In here

# For gcloud PVC
# ALpha is in us-central1-c
gcloud compute disks create example-disk --size=10Gi --zone=us-central1-c
# example-disk  us-central1-c  10       pd-standard  READY
# ssh and mount disk
gcloud compute ssh --zone "us-central1-a" "hot-diagnostics-alpha-remote-agent-azdo" --project "sales-engineering-emea"





#helm upgrade --install -f ./install/bring-your-own-observability-helm-values.yaml \
#astroshop \
#open-telemetry/opentelemetry-demo \
#--namespace astroshop \
#--set default.image.repository=geaksoteldemo.azurecr.io/oteldemo \
#--set default.image.tag=1.11.1 \
#--create-namespace \
#--version 0.32.8
# TODO to change for shinojosa (org in Docker)

helm upgrade --install -f ./install/bring-your-own-observability-helm-values.yaml \
astroshop \
open-telemetry/opentelemetry-demo \
--namespace astroshop \
--set default.image.repository=ghcr.io/open-telemetry/demo \
--set default.image.tag=1.11.1 \
--create-namespace \
--version 0.32.8

# https://github.com/keyoke/opentelemetry-demo/


kubectl apply -f dynakube-alpha.yml --namespace dynatrace

# TODO: is FluentBit needed? 
helm repo add fluent https://fluent.github.io/helm-charts
helm repo update


# Opentelemetry
helm repo add open-telemetry https://open-telemetry.github.io/opentelemetry-helm-charts
helm repo update
helm upgrade --install -f ./install/bring-your-own-observability-helm-values.yaml astroshop open-telemetry/opentelemetry-demo \
--namespace astroshop \
--set default.image.repository=ghcr.io/open-telemetry/demo \
--set default.image.tag=1.11.1 \
--create-namespace \
--version 0.32.8 


# Build the opentelemtery-demo application on host, need to export variables and set env.dockerhub file
export RELEASE_VERSION='1.11.1'
export IMAGE_NAME=shinojosa/oteldemo
export DEMO_VERSION='1.11.1'

# Make sure we label the k8s namespace for OA injection
kubectl label namespace astroshop dynatrace.com/inject=true

# Patch the astroshop deployments
bash patch.sh
#kubectl delete deployment astroshop-frontendproxy --namespace astroshop
#kubectl delete service astroshop-frontendproxy --namespace astroshop
