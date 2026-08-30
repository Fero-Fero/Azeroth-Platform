export const DEFAULT_VERIFICATION_SUBJECT = 'Verify your account'

export const DEFAULT_VERIFICATION_BODY_HTML = `<p>Thanks for signing up on {{siteName}}.</p>
<p><a href="{{verifyUrl}}">Verify your email address</a> to finish creating your account.</p>
<p>This link expires in {{expiryHours}} hours.</p>`

export const DEFAULT_ARMORY_EMAIL = {
  smtpHost: '',
  smtpPort: 587,
  smtpSecurity: 'starttls' as const,
  smtpUsername: '',
  smtpPassword: '',
  fromAddress: '',
  fromName: '',
  verificationSubject: DEFAULT_VERIFICATION_SUBJECT,
  verificationBodyHtml: DEFAULT_VERIFICATION_BODY_HTML,
}
