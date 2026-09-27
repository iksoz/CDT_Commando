"""Ansible filters used by the Commando deployment roles."""

from __future__ import annotations

import base64
import hashlib
import hmac
import ipaddress

from ansible.errors import AnsibleFilterError


def commando_host_key(master_key: str, host_name: str) -> str:
    """Derive the enrollment key expected by Commando for one policy host."""
    if not isinstance(master_key, str) or len(master_key) < 16:
        raise AnsibleFilterError(
            "commando_enrollment_key must be a string containing at least 16 characters"
        )
    if not isinstance(host_name, str) or not host_name.strip():
        raise AnsibleFilterError("commando_agent_name must be a non-empty string")

    message = f"Commando:host:{host_name.lower()}".encode("utf-8")
    digest = hmac.new(master_key.encode("utf-8"), message, hashlib.sha256).digest()
    return base64.b64encode(digest).decode("ascii")


def commando_host_addresses(inventory_hosts, hostvars) -> list[str]:
    """Return unique IPv4 policy addresses for a list of inventory hosts."""
    addresses: list[str] = []
    for inventory_name in inventory_hosts:
        variables = hostvars[inventory_name]
        value = variables.get(
            "commando_host_address", variables.get("ansible_host", inventory_name)
        )
        try:
            address = str(ipaddress.IPv4Address(str(value)))
        except ipaddress.AddressValueError as exception:
            raise AnsibleFilterError(
                f"Commando host '{inventory_name}' requires an IPv4 ansible_host "
                "or commando_host_address"
            ) from exception
        if address in addresses:
            raise AnsibleFilterError(f"Duplicate Commando policy address '{address}'")
        addresses.append(address)
    return addresses


def commando_host_networks(inventory_hosts, hostvars) -> list[str]:
    """Return exact /32 enrollment networks for inventory hosts."""
    return [f"{address}/32" for address in commando_host_addresses(inventory_hosts, hostvars)]


def commando_subject_alt_names(names) -> list[str]:
    """Convert DNS names and IP addresses into OpenSSL SAN entries."""
    entries: list[str] = []
    for value in names:
        text = str(value).strip()
        if not text:
            continue
        try:
            entry = f"IP:{ipaddress.ip_address(text)}"
        except ValueError:
            entry = f"DNS:{text}"
        if entry not in entries:
            entries.append(entry)
    if not entries:
        raise AnsibleFilterError("At least one Commando server certificate name is required")
    return entries


class FilterModule:
    """Expose Commando-specific filters to Ansible."""

    def filters(self):
        return {
            "commando_host_key": commando_host_key,
            "commando_host_addresses": commando_host_addresses,
            "commando_host_networks": commando_host_networks,
            "commando_subject_alt_names": commando_subject_alt_names,
        }

