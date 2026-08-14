# Amazon Web Services — Login & Setup Strategy

**Provider enum:** `CloudProvider.Aws`  
**Implementation order:** **#07** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** IAM access key + secret (manual paste)

---

## Summary

AWS does **not** offer a simple OAuth flow where a third-party SaaS obtains EC2 management tokens after a generic “Login with AWS” click (unlike DigitalOcean or Google Cloud user consent).

Practical alternatives for Azeroth Platform:

1. **Cross-account IAM role** (recommended “Connect AWS account” UX)
2. **IAM Identity Center (SSO) OIDC** (enterprise)
3. **Manual IAM access keys** (current Advanced path)

---

## Recommended login strategy: Cross-account IAM role

This is the AWS-industry-standard pattern for SaaS connecting to customer accounts.

### UX flow

| Step | Action |
|------|--------|
| 1 | User clicks **Connect AWS account** |
| 2 | Platform shows **CloudFormation / Terraform template** + unique **External ID** |
| 3 | User deploys stack in their AWS account (creates IAM role trusting platform account) |
| 4 | User pastes **Role ARN** (or template output) |
| 5 | Platform calls `sts:AssumeRole` with External ID → temporary credentials |
| 6 | Validate: `ec2:DescribeRegions` |
| 7 | UI: **✓ Account connected** + account alias / account id |
| 8 | Store: `{ roleArn, externalId }` encrypted — **no long-lived keys** |

### Why this fits “login” UX

- One guided flow instead of hunting for access keys
- Short-lived credentials (auto-rotate every hour)
- Customer revokes by deleting CloudFormation stack
- Scopes controlled by IAM policy on the role

### IAM policy on customer role (align with existing README)

Include permissions already documented in [`plans_support/01-cloud-integration-completed.md`](../plans_support/01-cloud-integration-completed.md):

- **Read:** `ec2:Describe*`, `ssm:Describe*`
- **Launch:** `ec2:RunInstances`, `ec2:ImportKeyPair`, …
- **SSM bootstrap:** `ssm:SendCommand`, …
- **SG sync (shipped):** `ec2:AuthorizeSecurityGroupIngress`, …

Provide **three policy tiers** in template:

- `AzerothPlatformReadOnly`
- `AzerothPlatformStandard` (list + SSM bootstrap)
- `AzerothPlatformFull` (+ create + SG sync)

---

## Alternative: IAM Identity Center OIDC (enterprise)

For orgs using AWS IAM Identity Center:

| Step | Action |
|------|--------|
| 1 | Customer configures Identity Center trusted token issuer |
| 2 | Platform OIDC app registered as customer-managed app |
| 3 | User signs in via corporate IdP |
| 4 | Token exchange → Identity Center token → AWS API |

**Complexity:** High. Defer until enterprise demand. Document in Advanced docs only.

---

## Fallback: Manual access keys (current)

`AuthMethod=Manual` — paste access key ID + secret. Existing `AwsEc2Client` / `AwsSsmClient` unchanged.

---

## Setup dialog strategy

Works identically regardless of auth method once credentials/session exist.

### Tab A — Existing EC2 instance

- `DescribeInstances` — running, public IP
- **SSM bootstrap path:** verify instance registered with SSM
- Select → host + instance id + region

### Tab B — Create EC2 instance (shipped)

- Launch catalog: regions, instance types, AMIs
- `RunInstances` + user_data + key pair import
- Default VPC requirement (documented)

### Security automation (shipped for AWS)

- **Sync security group** on stack overview: `POST /api/stacks/{id}/sync-cloud-security-group`
- OAuth/role auth must include SG permissions
- Maps stack profile ports → EC2 security group ingress with admin CIDR for SSH

---

## Part 2 — Automatic security group setup

**Default-on** when configuring an EC2 instance (pick existing or launch new).

| Step | Action |
|------|--------|
| 1 | Prompt admin SSH CIDR before launch/sync |
| 2 | **Launch:** create SG with SSH **admin CIDR only** (fix today’s 0.0.0.0/0 bootstrap SG) |
| 3 | Apply full profile ingress: auth, world, armory, client from `VpcSecurityCatalog` |
| 4 | Attach SG to instance; skip duplicate rules (idempotent) |
| 5 | **Pick existing:** resolve instance by id or public IP → same sync |

**IAM (AssumeRole or keys):** `ec2:AuthorizeSecurityGroupIngress`, `ec2:DescribeSecurityGroups`, `ec2:ModifyInstanceAttribute` (if replacing SG).

**Re-sync:** Stack overview panel + port changes + “Update admin IP”.

See [`../plans_support/09-cloud-security-group-providers.md`](../plans_support/09-cloud-security-group-providers.md).

---

## SSH hardening (operator user + final lockdown)

See master plan: [SSH access model](./02-cloud-oauth-login-master-plan.md#ssh-access-model--dedicated-operator-user--final-hardening).

### During setup — always create operator user

| Step | AWS-specific |
|------|--------------|
| Launch / user_data | Extend bootstrap to `useradd` operator user (e.g. `azp-admin`), add platform SSH key to **that user**, install Docker + sudo |
| Wizard SSH user | Store operator username on stack — **not** `root`, not long-term `ubuntu` |
| Key pair / import | EC2 key pair or vault key authorized for operator user only |
| Test connection | Platform SSH as operator user from admin CIDR |

Default Ubuntu AMIs ship with `ubuntu` + launch key. Bootstrap must **move** platform access to the operator user before hardening.

### Final step — after stack fully working

**Trigger:** Last wizard step or stack VPC overview — **Finalize SSH hardening** (confirmation required).

**Goal:**

| Account | Remote SSH from your PC / internet | AWS console break-glass |
|---------|-----------------------------------|-------------------------|
| **root** | ❌ Never | SSM Session Manager / serial console (emergency) |
| **ubuntu** | ❌ Never from remote | ✅ **EC2 Instance Connect** — Connect → EC2 → instance → **Connect** → **EC2 Instance Connect** tab |
| **Operator user** (setup) | ✅ From admin CIDR only | N/A — normal SSH with saved key |

**Host configuration (applied via SSH as operator user):**

1. **`PermitRootLogin no`** in `/etc/ssh/sshd_config.d/99-azeroth-platform-hardening.conf`
2. **Remove platform keys from `ubuntu`** — empty or remove `~ubuntu/.ssh/authorized_keys` entries added at launch (do not delete EC2 Instance Connect setup)
3. **EC2 Instance Connect for `ubuntu` only** — ensure `ec2-instance-connect` package present (Ubuntu AMIs often preinstall). Use `AuthorizedKeysCommand` for `ubuntu`:

   ```
   Match User ubuntu
       AuthorizedKeysCommand /usr/share/ec2-instance-connect/eic_run_authorized_keys %u %f
       AuthorizedKeysCommandUser ec2-instance-connect
   ```

   This allows AWS console Instance Connect to inject short-lived keys while blocking static keys from remote PCs.

4. **`AllowUsers <operator-user> ubuntu`** — `ubuntu` remains in list **only** for Instance Connect path above; operator user for normal pubkey auth. Alternatively use `AllowUsers <operator-user>` if Instance Connect is configured via IAM + EIC without listing `ubuntu` (test on target AMI).

5. **`PasswordAuthentication no`**
6. **`systemctl reload sshd`** — verify platform test connection still works as operator user before closing session.

**Security group:** SSH (22) remains open only to **admin CIDR** — Instance Connect browser still reaches port 22 from operator browser, but `ubuntu` accepts no static remote keys.

**Setup dialog checklist item:** “I understand `ubuntu` is console-only (Instance Connect); daily access is via my operator user.”

### Implementation checklist (SSH)

- [ ] Bootstrap creates operator user + key on `RunInstances` user_data
- [ ] Wizard validates SSH username (reject `root`, warn on `ubuntu` as long-term choice)
- [ ] Final hardening API step + UI
- [ ] AMI test matrix: Ubuntu 22.04/24.04 with EC2 Instance Connect after hardening
- [ ] Document recovery: Instance Connect → `ubuntu` → `sudo -u <operator-user> -i` if operator key lost

---

## Isolation from other providers

- **`AwsCrossAccountAuthStrategy`** — AssumeRole only; no token format shared with GCP/Azure
- Platform AWS account ID in config (`Cloud:Aws:PlatformAccountId`)
- External ID generated per connection (UUID)
- Session credentials cached in-memory with expiry — never persist access keys from AssumeRole
- Separate from DO/Vultr OAuth callback routes entirely

---

## Optional: “Login with AWS” confusion guard

Do **not** label AssumeRole flow as “OAuth” in UI. Use:

- **Connect AWS account** (primary)
- **Advanced: IAM access keys**

If AWS Login app (Builder ID) is ever requested, clarify it does **not** grant EC2 API access to third parties without IAM setup.

---

## Implementation checklist

- [ ] CloudFormation template + External ID generator
- [ ] `AwsCrossAccountAuthStrategy.AssumeRoleAsync`
- [ ] Credential cache with STS expiry refresh
- [ ] Connection entity: `{ roleArn, externalId, authMethod: AssumedRole }`
- [ ] UI: Connect wizard + checkmark + account id display
- [ ] Setup dialog (existing instance + create)
- [ ] Verify SG sync works with assumed role credentials
- [ ] Enterprise doc stub for Identity Center OIDC
- [ ] Operator user bootstrap + final SSH hardening (Instance Connect for `ubuntu`)

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| Customer misconfigures trust policy | Template + validation step with clear errors |
| No default VPC | Already documented; surface in launch UI |
| SSM requires instance profile | Checklist in setup dialog for bootstrap path |
| STS session 1h limit | Auto re-assume before API calls |
| Not “one-click OAuth” | Set expectations in UI copy |

---

## References

- [IAM AssumeRole](https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use.html)
- [IAM Identity Center trusted token issuers](https://docs.aws.amazon.com/singlesignon/latest/userguide/using-apps-with-trusted-token-issuer.html)
- Existing: `AwsEc2Client.cs`, `AwsSsmClient.cs`, `CloudFirewallService.cs`
