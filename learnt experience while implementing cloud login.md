# Learnt experience while implementing cloud login

Working notes from implementing cloud account connect, instance launch, and VPC verify (Aug 2026). This is not a plan. Plans stay in `plans/` and `plans_support/`. Use this file so the next pass does not re-learn the same traps.

Related plans: `plans/02-cloud-oauth-login-master-plan.md`, `plans/07-cloud-login-aws.md`, `plans_support/09-cloud-security-group-providers.md`, `plans_support/10-wow-vpc-network-security.md`.

---

## 1. "Login" is not one OAuth flow

Operators say "cloud login." Providers do not share one identity model. Label the UI by what the provider actually does.

| Provider | What "Connect" really is | Do not call it |
|----------|--------------------------|----------------|
| DigitalOcean, Vultr, GCP, Azure | OAuth 2.0 (popup / redirect, PKCE where required) | Generic "token paste" |
| AWS | Cross-account **IAM role** + `sts:AssumeRole` + External ID | OAuth, "Login with AWS" |
| Hetzner | Guided API token | OAuth |

Shared architecture that worked:

- `ICloudProviderAuthStrategy` per provider. No shared OAuth state across providers.
- Normalized connection DTO (`AuthMethod`: Manual / OAuth / AssumedRole, account hint, expiry, `NeedsReauth`).
- Manual paste stays under **Advanced**. Do not delete it; CI and power users need it.
- AWS Connect wizard: CloudFormation YAML (Read only / Standard / Full) + External ID + paste Role ARN. Reconnect is for assumed-role connections, not an OAuth refresh.

Store `{ roleArn, externalId }` encrypted. STS session keys are cached ~1 hour and **never persisted**. Pasted access keys still work for AWS under Advanced.

Platform AWS account id for AssumeRole trust lives in `CloudOAuth:Aws:PlatformAccountId` (12 digits). Without it, Connect AWS cannot start.

---

## 2. IAM is not a security group

This confusion came up repeatedly. Keep the words separate in UI, APIs, and docs.

| Term | What it is | When it is used |
|------|------------|-----------------|
| **IAM role / user / access keys** | Who may call AWS APIs | Connect account (Part 2B). Needed to launch and to change SGs. |
| **EC2 security group** | Cloud **network** allow/deny (ports, CIDRs) | Launch + Verify (Part 2A). Lives on the instance ENI. |
| **ufw** | Host firewall on the VM | Launch user-data + Verify (Layer 2). |
| **Instance profile** | IAM identity **on the VM** (SSM, etc.) | Optional SSM bootstrap. Not created as part of "setup VPC." |

Do **not** create IAM roles as a side effect of "setup VPC" or "apply firewall." Do **not** persist cloud-account **root** keys for daily API use.

"Security groups for IAM roles" in operator language almost always meant **EC2 security groups**, not IAM.

---

## 3. Product flow that actually works

Launch must do setup. Verify must check both sides. Do not ask the operator to Configure vs Skip after a successful launch.

### Launch server (automated)

On create, apply both layers in one shot:

1. **Host (cloud-init user-data):** Docker Engine + Compose, docker group, passwordless sudo, unattended-upgrades, ufw (deny incoming, allow SSH + game/web defaults).
2. **Provider (AWS today):** security group `azeroth-platform-launch` with the same ports.

Default ports at launch (stack ports are chosen later in the wizard):

- Allow: SSH 22, auth 3724, world 8085, armory 8100, client 8101
- Never open: MySQL 3306, SOAP 7878

SSH source: admin CIDR when the operator sets it (Use my IP); otherwise `0.0.0.0/0`. Empty CIDR must not crash launch. Long-term goal from plan 09 is admin CIDR only for SSH; do not leave 22 world-open after bootstrap if a CIDR is known.

Launch API returns when the **instance exists**. User-data is async. Verify VPC can fail for 1-2 minutes until cloud-init finishes. That is expected, not a broken launch.

See [section 6](#6-ubuntu-2404-sudo--docker-verify-fails-with-a-password-is-required) if SSH and the cloud SG pass but Docker Engine fails with `sudo: a password is required`.

### After launch: certificate, then Verify VPC

1. Download the generated `.pem` (single Launch button should download and close the dialog; keep a fallback download on the selected-host panel).
2. **Verify certificate:** operator uploads the downloaded `.pem`, match against the in-memory key, then wipe the PEM from memory and hide download. Form flag `deployment.sshCertificateVerified` defaults `true`; set `false` after a launch that generated a key. Gate continue on this.
3. **Verify VPC** (was Setup VPC): SSH + Docker + ufw + OS baselines on the VM, plus AWS SG probe on the instance. Continue requires `connectionVerified`. Configure vs Skip is gone.

**Repair host setup** stays for existing VMs and failed cloud-init. After repair, Verify VPC again. Existing / Remote host has no user-data; Verify will fail until Repair.

In-product, "VPC" usually means the remote game host, not "create an AWS VPC from scratch." Launch creates a VM and attaches a security group. Do not promise a full VPC/subnet wizard unless that work is actually built.

---

## 4. Two firewall layers, one rule catalog

Defense in depth (plan 10):

1. Cloud edge (SG / NSG / Cloud Firewall)
2. Host ufw
3. Docker publish bind (management ports not on `0.0.0.0`)
4. SSH / OS hardening (Part 2C, still largely planned)

Single source of truth: `VpcSecurityCatalog`. Launch uses `BuildLaunchCloudIngressRules(adminSourceCidr)`. Placeholder `your-ip/32` is replaced with the admin CIDR (or `0.0.0.0/0` if empty).

Verify must check **both**:

- Host: `RemoteEngineService` after Docker (`unattended-upgrades`, ufw active, allow 22/3724/8085/8100/8101, deny 3306/7878).
- Cloud: `POST /api/cloud/connections/{id}/firewall-probe` (`ICloudFirewallService.ProbeLaunchSecurityGroupAsync`). AWS-only today; other providers should return a clear "AWS only" check, not a fake pass.

`ufw --force reset` in user-data can drop SSH if enable fails. Allow SSH **before** `ufw --force enable`. Treat lockout as a real risk.

---

## 5. AWS API and SDK traps

### Security group descriptions are ASCII-only

EC2 `GroupDescription` and rule descriptions reject non-ASCII. An em dash (`—`) in "Azeroth Platform launch — …" caused `InvalidCharacter`. Always run descriptions through `ToAwsAscii` (hyphen, straight quotes). Keep user-data `echo` strings ASCII too.

### Duplicate ingress is not a failure

`AuthorizeSecurityGroupIngress` on an existing rule throws. Ignore duplicate-rule errors when creating/updating `azeroth-platform-launch`.

### AWS SDK for .NET v2 types

`IpPermission.FromPort` / `ToPort` can be `int`, not `int?`. `permission.FromPort ?? 0` does not compile. Use the value directly.

### AssumeRole vs instance credentials

All EC2/SSM/firewall calls must go through `AwsCredentialResolver` so assumed-role sessions and pasted keys both work. Do not construct `BasicAWSCredentials` only from stored access keys.

### CloudFormation YAML in C#

Interpolated strings (`$"""` / `{0}`) fight CloudFormation `{Ref}` and IAM JSON. Use a raw `$$"""` string (or equivalent) so account id, External ID, and IAM actions stay literal. Brace-escaping the template by hand will break.

Policy tiers that matched the product:

- Read only: list regions / instances
- Standard: + SSM bootstrap
- Full: + `RunInstances`, key import, SG authorize

### Instance list vs launch vs SSM

**Use existing VM** should list **running** instances with a public IP or public DNS. Stopped, pending, or private-only hosts look like "missing" VMs. A Refresh button is required; do not make the operator close the dialog.

**SSM bootstrap** is a third AWS path: push user-data-equivalent via Systems Manager when the agent + instance profile exist but SSH does not. It is not a substitute for Create new VM or Use existing + SSH Repair. Do not surface it as a unique wizard step unless the operator cannot SSH.

---

## 6. Ubuntu 24.04 sudo — Docker Verify fails with "a password is required"

**Symptom that fooled us:** SSH passed. Cloud security group matched the launch profile. Docker Engine failed with:

`sudo: a password is required`

`SSH works, but the remote Docker engine is not available.`

ufw and OS baseline checks looked like they passed (or never appeared). They **never ran**. `TestConnectionAsync` returned as soon as Docker failed. The AWS SG probe is a separate API call from the frontend, so it can succeed while the host is not actually ready.

**Root cause:** Ubuntu 24.04 sudo ships `Defaults use_pty`. Over SSH without a TTY, `sudo -n` then fails with "a password is required" **even when NOPASSWD is already set** (AWS cloud-init writes `/etc/sudoers.d/90-cloud-init-users` with `ubuntu ALL=(ALL) NOPASSWD:ALL`). Launch user-data originally wrote only that NOPASSWD line to `90-azeroth-platform`. First Time Setup / Repair already knew the real fix and wrote extra Defaults. Launch did not.

Verify's Docker check was:

1. `/usr/bin/docker info` (fails if this SSH session is not yet in the `docker` group after `usermod`)
2. `sudo -n /usr/bin/docker info` (fails on Ubuntu 24.04 because of `use_pty`)

The UI showed only the last error (sudo password), so it looked like Docker was missing or sudo was never configured.

**Required sudoers** (shared helper `VpcBootstrapUserData.BuildPasswordlessSudoers`, file `/etc/sudoers.d/99-azeroth-platform`, mode 440, validate with `/usr/sbin/visudo -c`):

```
Defaults !use_pty
Defaults:<user> !use_pty
Defaults !requiretty
Defaults:<user> !requiretty
<user> ALL=(ALL) NOPASSWD:ALL
```

NOPASSWD alone is not enough. `!use_pty` and `!requiretty` are the actual fix for `sudo -n` over SSH.

**What the code must do:**

| Path | Behavior |
|------|----------|
| Launch user-data | Write the full sudoers file above (not NOPASSWD-only). |
| Repair host setup | `EnsurePasswordlessSudoAsync` — if `sudo -n true` fails, install the file using one TTY `sudo` (AWS `ubuntu` can do that without a password). |
| Verify VPC | Call `EnsurePasswordlessSudoAsync` **before** the Docker check so already-launched VMs recover without a new instance. Also try `sg docker -c 'docker info'` so group membership works without sudo. |
| Error copy | Do **not** say "Run bootstrap script in step 2" or "First Time Setup in step 4". Those steps are gone. Say: wait for user-data, then Verify again; or use **Repair host setup**. |

**Recovering a VM launched with the old user-data:** restart the API (so Verify writes sudoers), click Verify VPC again. Do not require a new AMI. If Docker is still down, wait for cloud-init (`/var/log/cloud-init-output.log`, `/var/lib/azeroth-platform/bootstrap-ready`) and retry.

**Also remember:** `usermod -aG docker` does not affect the current SSH session. Each Verify is a new session, so group membership should apply after cloud-init finishes — unless PAM/supplementary groups are skipped, which is why `sg docker` exists.

---

## 7. SSH keys and PEM handling

- Generate at launch when no saved key is selected; store in the vault; return PEM once.
- Browser download: `downloadPemFile` / `pemDownloadFilename`. Normalize PEM (`BOM`, `\r\n`) before fingerprinting.
- After the operator verifies the uploaded file matches, **wipe in-memory PEM**. Do not keep a second download forever on the page.
- `sshCertificateVerified === false` must block wizard continue even if SSH later works with a vaulted key — the point is proving they saved the file.

---

## 8. Frontend and wizard traps

### Catalog fields look dead while loading

Region / size / AMI populate after a network round-trip. Show a spinner and a "Loading…" option (`CatalogField`). An empty disabled select reads as broken.

### `vpcSetupMode` watch drove compose, then broke it

A form watch on `deployment.vpcSetupMode` was used to trigger `docker compose up -d --build` side effects. Dropping that watch for the Verify VPC UI stopped compose rebuilds. If a watch is the only trigger, restoring the UI is not enough — restore the watch or move the side effect somewhere explicit.

### Wizard gating

Continue on External requires:

1. Certificate verified (if a launch key was generated)
2. Verify VPC passed (`connectionVerified`)

Do not require Configure vs Skip. Default `vpcSetupMode` to `skip` after launch so old gates do not block. `cloudSecurityGroupAcknowledged` only mattered on the old Configure path.

### `noUnusedLocals`

`frontend/tsconfig.app.json` has `noUnusedLocals: true`. Removing Configure/Skip JSX leaves helpers, icons, and queries unused and **fails `tsc`**. Either delete dead UI or keep it compiled. Do not leave a half-removed Setup VPC block.

### Do not rewrite large TSX with PowerShell `Set-Content`

That path corrupted UTF-8 (em dashes / arrows / smart quotes became mojibake). Edit with the normal file tools, or Python with UTF-8. After a PowerShell rewrite, search for `â` and `Ã`.

### Launch options must actually be sent

The setup dialog already had "Apply network profile" and admin CIDR. They did nothing until `CloudLaunchPanel` posted `applyNetworkProfile` and `adminSourceCidr`. UI without API fields is a silent no-op.

---

## 9. Provider coverage (honest)

| Surface | Status |
|---------|--------|
| AWS AssumeRole connect + pasted keys | Shipped |
| AWS launch user-data (Docker, ufw, baselines) | Shipped |
| AWS SG create/update at launch + probe on Verify | Shipped |
| OAuth shell (status, popup, Advanced paste, setup dialog) | Shipped; most providers `IsImplemented = false` until their plan |
| DigitalOcean / GCP / Azure / Vultr / Hetzner auto-SG | Not implemented. Probe should say so. User-data still runs where the provider injects it. |
| Part 2C (lock root/`ubuntu` out of internet SSH; console break-glass only) | Planned, not done. Do not claim it in Verify yet. |

Isolation still holds: one auth strategy and one firewall strategy per provider. Shared UI consumes DTOs only.

---

## 10. Process notes that saved time

- Implement AWS Connect first and leave other OAuth providers unimplemented so the AssumeRole path can be tested in isolation.
- Restart the API after backend credential / EC2 / user-data changes. Rebuild frontend after wizard changes (`docker compose up -d --build` if that is how the stack is served).
- Cloud-init logs on the instance (`/var/log/cloud-init-output.log`) and `/var/lib/azeroth-platform/bootstrap-ready` are the debug path when Verify fails immediately after launch.
- Never commit secrets (`.pem`, access keys, `appsettings` with real `PlatformAccountId` keys). `PlatformAccountId` itself is not a secret; access keys in the same block are.

---

## 11. What we would do the same way again

- Strategy pattern for auth; never one OAuth helper with a provider switch.
- Apply host + cloud firewall at **launch**, then make the wizard step **Verify**, not Configure.
- One catalog for ports; deny 3306/7878 by omission on both layers.
- ASCII-sanitize anything that goes to AWS descriptions.
- Treat launch success as "VM exists," not "host is ready."
- Keep Repair for brownfield VMs; do not pretend user-data ran on an existing machine.
- Share one sudoers blob (`BuildPasswordlessSudoers`) between launch user-data, Verify, and Repair. Include `!use_pty` and `!requiretty`, not just NOPASSWD.
- Keep Advanced credential paste.

## 12. What we would not do again

- Call AWS Connect "OAuth."
- Create IAM instance profiles as part of "setup VPC."
- Open MySQL or SOAP because "the stack uses those ports."
- Gate the wizard on Configure vs Skip after launch already applied setup.
- Write only `{user} ALL=(ALL) NOPASSWD:ALL` and assume `sudo -n docker` works on Ubuntu 24.04 (`Defaults use_pty` still fails).
- Return early from Verify on Docker failure and treat a passing AWS SG probe as "the host is ready."
- Point operators at deleted wizard steps (bootstrap script / First Time Setup) when sudo or Docker fails.
- Truncate `DeploymentStep.tsx` with PowerShell and hope encoding survives.
- Drop a React `watch` that other effects depend on without replacing the trigger.
- Assume SG probe works for non-AWS providers.
- Assume Verify will pass in the same second as Launch.
