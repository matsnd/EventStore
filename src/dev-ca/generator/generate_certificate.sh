#!/bin/bash

if [ "$#" -ne 5 ]; then
    echo "generate_cert.sh <certificate_name> <common_name> <ip_addresses> <dns_names> <password>"
    echo "generate_cert.sh dev eventstore.localhost.com IP:127.0.0.1,IP:192.168.1.1 DNS:eventstore.localhost.com,DNS:node.localhost.com changeit"
    exit
fi

certificate_name=$1
common_name=$2
ip_addresses=$3
dns_names=$4
password=$5

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
pushd DIR &>/dev/null

echo "Creating certificate directory: $certificate_name"
mkdir -p "$certificate_name"
pushd "$certificate_name" &>/dev/null

# Generate Certificate Authority
openssl genrsa -out ca.key 2048 &>/dev/null
openssl req -new -sha256 -key ca.key -subj "/OU=Development/O=Event Store Ltd/CN=$common_name" -out ca.csr &>/dev/null
openssl req \
    -x509 \
    -key ca.key \
    -in ca.csr \
    -out ca.crt \
    -hours 87600 \
    -sha256

# Generate Server Certificate from CA
echo "Generating key: $certificate_name.key"
openssl genrsa -out "$certificate_name".key 2048 &>/dev/null

echo "Generating CSR: $certificate_name.csr"
openssl req -new -sha256 -key "$certificate_name".key -subj "/OU=Development/O=Event Store Ltd/CN=$common_name" -out "$certificate_name".csr  &>/dev/null

echo "Generating Certificate: $certificate_name.crt"
openssl x509 \
    -req \
    -in "$certificate_name".csr \
    -CA ca.crt \
    -CAkey ca.key \
    -CAcreateserial \
    -out "$certificate_name".crt \
    -hours 43800 \
    -extfile <(\
    printf "subjectAltName = $ip_addresses,$dns_names") \
    -sha256

echo "Generating PKCS#12 certificate: $certificate_name.p12"
openssl pkcs12 -export -inkey "$certificate_name".key -in "$certificate_name".crt -out "$certificate_name".p12 -password pass:$password  &>/dev/null

echo "Deleting CSR, certificate and key file"
rm "$certificate_name".csr &>/dev/null
rm "$certificate_name".crt &>/dev/null
rm "$certificate_name".key &>/dev/null
rm .srl &>/dev/null

popd &>/dev/null

popd &>/dev/null

echo "Done!"