r"""Reads the store credentials from Azure Key Vault.

Everyone signs in with their own Azure account (`az login`, or the Sign in button in the
window), so no key or credential file is ever stored in the project folder.

The vault must hold these secrets (names may only use letters, digits and hyphens):

    apple-key-id            App Store Connect API key ID
    apple-issuer-id         App Store Connect issuer ID
    apple-p8                the full text of the AuthKey_XXXX.p8 file
    google-service-account  the full service account JSON
    playfab-title-id        PlayFab title ID
    playfab-secret-key      PlayFab secret key

The vault name comes from `keyvault_name` in upload_config.json (or --vault on each script).
Install: pip install azure-identity azure-keyvault-secrets
"""
import json
import os
from pathlib import Path

HERE = Path(__file__).resolve().parent

APPLE_KEY_ID = "apple-key-id"
APPLE_ISSUER_ID = "apple-issuer-id"
APPLE_P8 = "apple-p8"
GOOGLE_SERVICE_ACCOUNT = "google-service-account"
PLAYFAB_TITLE_ID = "playfab-title-id"
PLAYFAB_SECRET_KEY = "playfab-secret-key"

NEEDED = {
    "apple": [APPLE_KEY_ID, APPLE_ISSUER_ID, APPLE_P8],
    "google": [GOOGLE_SERVICE_ACCOUNT],
    "playfab": [PLAYFAB_TITLE_ID, PLAYFAB_SECRET_KEY],
}

SIGN_IN_HINT = "Sign in with `az login` (or the Sign in to Azure button in the window)."


class SecretError(Exception):
    """A problem reading from the vault, worded so it can be shown to the user as is."""


def default_vault_name():
    """The vault named in upload_config.json next to this file, else AZURE_KEYVAULT_NAME."""
    try:
        data = json.loads((HERE / "upload_config.json").read_text(encoding="utf-8-sig"))
        name = data.get("keyvault_name")
    except (OSError, ValueError):
        name = None
    return name or os.environ.get("AZURE_KEYVAULT_NAME")


class Vault:
    def __init__(self, name=None):
        name = (name or default_vault_name() or "").strip()
        if not name or name.startswith("YOUR_"):
            raise SecretError("No Key Vault name set. Put it in upload_config.json as "
                              '"keyvault_name" (Settings... in the window), or pass --vault.')
        try:
            from azure.identity import AzureCliCredential
            from azure.keyvault.secrets import SecretClient
        except ImportError:
            raise SecretError("The Azure packages are missing. Run:\n"
                              "  pip install azure-identity azure-keyvault-secrets")
        self.name = name
        self.url = f"https://{name}.vault.azure.net"
        self._client = SecretClient(vault_url=self.url, credential=AzureCliCredential())
        self._cache = {}

    def get(self, secret):
        if secret in self._cache:
            return self._cache[secret]
        from azure.core.exceptions import (ClientAuthenticationError, HttpResponseError,
                                           ServiceRequestError)
        try:
            value = self._client.get_secret(secret).value
        except ClientAuthenticationError as e:
            detail = str(e).strip().splitlines()[0] if str(e).strip() else ""
            raise SecretError(f"Not signed in to Azure. {SIGN_IN_HINT}" + (f"\n  ({detail})" if detail else ""))
        except ServiceRequestError:
            raise SecretError(f"Could not reach {self.url}. Check the vault name "
                              f"({self.name!r}) and your internet connection.")
        except HttpResponseError as e:
            if e.status_code == 404:
                raise SecretError(f"Secret {secret!r} was not found in vault {self.name!r}. "
                                  "Check the secret name in the Azure portal.")
            if e.status_code in (401, 403):
                raise SecretError(f"Your account can't read secrets in vault {self.name!r}. "
                                  "Ask the vault owner to give you the 'Key Vault Secrets User' role "
                                  "(or check the vault's firewall settings).")
            raise SecretError(f"Azure Key Vault error reading {secret!r}: HTTP {e.status_code}")
        if value is None or not value.strip():
            raise SecretError(f"Secret {secret!r} in vault {self.name!r} is empty.")
        value = value.lstrip("﻿")
        self._cache[secret] = value
        return value

    def get_text(self, secret):
        return self.get(secret).strip()
