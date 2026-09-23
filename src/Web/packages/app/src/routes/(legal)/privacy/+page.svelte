<script lang="ts">
  import { resolve } from "$app/paths";
</script>

<svelte:head>
  <title>Privacy Policy - Nocturne</title>
</svelte:head>

<h1>Privacy Policy</h1>

<p class="lead">
  Your privacy matters. This policy explains how Nocturne handles your data and protects your privacy.
</p>

<h2>1. Introduction</h2>

<p>
  Nocturne is a self-hosted, open-source diabetes data management platform. This means <strong>you control where your data lives</strong>. Unlike cloud-based services, Nocturne runs on your own server or hosting provider, and your data stays in your own database.
</p>

<p>
  This privacy policy explains what data Nocturne collects (spoiler: very little), how it's stored, and your rights.
</p>

<h2>2. Data We DON'T Collect</h2>

<p>
  Because Nocturne is self-hosted, we (the Nocturne project developers) do not have access to your data. Specifically:
</p>

<ul>
  <li><strong>No analytics:</strong> Nocturne does not send usage analytics or telemetry to us by default.</li>
  <li><strong>No tracking:</strong> We don't track your usage, browsing, or behavior.</li>
  <li><strong>No data collection:</strong> Your glucose readings, treatments, meals, and health data never leave your server unless you choose to share them.</li>
  <li><strong>No selling data:</strong> We don't sell your data because we don't have access to it in the first place.</li>
  <li><strong>No cloud storage:</strong> Nocturne doesn't store your data in our cloud or servers. It's all on your infrastructure.</li>
</ul>

<h2>3. Data Storage</h2>

<p>
  All your Nocturne data is stored in your own database on your own server or hosting provider. This includes:
</p>

<ul>
  <li>Glucose readings (blood sugar, CGM data)</li>
  <li>Treatments (insulin, medications)</li>
  <li>Meals and nutrition data</li>
  <li>Activity and exercise logs</li>
  <li>User account information</li>
  <li>Settings and preferences</li>
</ul>

<p>
  <strong>Where your data lives:</strong>
</p>

<ul>
  <li><strong>Your database:</strong> All persistent data is stored in a PostgreSQL database that you control.</li>
  <li><strong>Your hosting provider:</strong> Your hosting provider's privacy policy and security practices apply to your data. Choose a provider you trust.</li>
  <li><strong>Browser local storage:</strong> Some temporary data (like UI preferences) may be stored in your browser's local storage. This never leaves your device.</li>
</ul>

<h2>4. Third-Party Services and Connectors</h2>

<p>
  Nocturne can connect to third-party diabetes data services to import your glucose and treatment data. When you use these integrations:
</p>

<ul>
  <li><strong>Third-party privacy policies apply:</strong> Services like Dexcom, FreeStyle Libre, Glooko, and Tidepool have their own privacy policies. Review them before connecting.</li>
  <li><strong>We don't control third parties:</strong> We cannot control what data these services collect about you or how they use it.</li>
  <li><strong>Credentials storage:</strong> Your API keys and login credentials for third-party services are stored <strong>encrypted</strong> in your database. Only your Nocturne instance can decrypt them.</li>
  <li><strong>Data transfer:</strong> When you connect to a third-party service, data flows between that service and your Nocturne instance. We (the Nocturne developers) do not see or have access to this data.</li>
</ul>

<p>
  <strong>Third-party services you might connect:</strong>
</p>

<ul>
  <li>Dexcom (CGM data)</li>
  <li>FreeStyle Libre (CGM data)</li>
  <li>Glooko (multi-source data aggregation)</li>
  <li>Tidepool (open diabetes data platform)</li>
  <li>Nightscout (diabetes data platform)</li>
  <li>Others via community plugins</li>
</ul>

<h2>5. Optional Analytics and Error Reporting</h2>

<p>
  In the future, Nocturne may offer optional, opt-in features for:
</p>

<ul>
  <li><strong>Error reporting:</strong> Send crash reports and error logs to help us improve Nocturne</li>
  <li><strong>Anonymous usage analytics:</strong> Aggregated, anonymized usage statistics</li>
</ul>

<p>
  If we add these features:
</p>

<ul>
  <li>They will be <strong>opt-in only</strong> - disabled by default</li>
  <li>You'll have clear control to enable or disable them</li>
  <li>We'll clearly explain what data would be collected</li>
  <li>Data will be anonymized and aggregated when possible</li>
  <li>No health data or personally identifiable information will be included</li>
</ul>

<p>
  <strong>Current status:</strong> As of this writing, Nocturne does not include any analytics or error reporting features.
</p>

<h2>6. Your Rights and Data Control</h2>

<p>
  Because you self-host Nocturne, you have complete control over your data:
</p>

<ul>
  <li><strong>You own your data:</strong> All data belongs to you. We make no claims to it.</li>
  <li><strong>Export anytime:</strong> You can export your data at any time. It's just a PostgreSQL database - you have full access.</li>
  <li><strong>Delete anytime:</strong> You can delete any or all of your data whenever you want.</li>
  <li><strong>Control access:</strong> You decide who can access your Nocturne instance and what they can see.</li>
  <li><strong>Move your data:</strong> You can migrate your database to a different server or hosting provider at any time.</li>
  <li><strong>Backup your data:</strong> You're responsible for backing up your database. We recommend regular backups.</li>
</ul>

<h2>7. Security</h2>

<p>
  Nocturne is designed with security best practices:
</p>

<ul>
  <li><strong>Encryption:</strong> API keys and sensitive credentials are stored encrypted in the database.</li>
  <li><strong>HTTPS recommended:</strong> We strongly recommend running Nocturne behind HTTPS to encrypt data in transit.</li>
  <li><strong>Authentication:</strong> User accounts are protected with password hashing (bcrypt).</li>
  <li><strong>Open source:</strong> The code is open for security review by the community.</li>
</ul>

<p>
  <strong>Your responsibility:</strong> As a self-hosted application, you're responsible for:
</p>

<ul>
  <li>Keeping your server and dependencies updated</li>
  <li>Using strong passwords</li>
  <li>Securing your hosting environment</li>
  <li>Configuring HTTPS/SSL certificates</li>
  <li>Regular backups</li>
</ul>

<h2>8. Children's Privacy</h2>

<p>
  Nocturne is often used to manage diabetes data for children. Because the software is self-hosted:
</p>

<ul>
  <li>Parents/guardians control all data for their children</li>
  <li>No data is sent to us (the developers)</li>
  <li>All data stays within the family's or caregiver's control</li>
</ul>

<p>
  If you're hosting Nocturne for your child, you have full control and responsibility for their data privacy and security.
</p>

<h2>9. Changes to This Privacy Policy</h2>

<p>
  We may update this privacy policy from time to time. When we do:
</p>

<ul>
  <li>The updated policy will be posted at this URL</li>
  <li>The "Last updated" date will change</li>
  <li>Major changes will be announced in the GitHub repository</li>
</ul>

<p>
  Because Nocturne is self-hosted, privacy policy changes don't affect data you've already collected - it stays under your control regardless.
</p>

<h2>10. Contact</h2>

<p>
  For questions about this privacy policy or Nocturne's privacy practices:
</p>

<ul>
  <li>GitHub: <a href="https://github.com/nightscout/nocturne" target="_blank" rel="noopener noreferrer">github.com/nightscout/nocturne</a></li>
  <li>Open an issue or discussion on GitHub</li>
</ul>

<p>
  For questions about your specific Nocturne instance's privacy and data handling, contact your instance administrator (which might be you if you're self-hosting).
</p>

<h2>Summary</h2>

<p>
  The key point: <strong>Nocturne is self-hosted, which means you control your data</strong>. We (the developers) don't collect it, don't have access to it, and can't sell it or share it. Your privacy is protected by design.
</p>

<p>
  See also: <a href={resolve("/terms")}>Terms of Service</a>
</p>

<hr />

<p class="text-sm text-muted-foreground">
  This privacy policy is effective as of February 21, 2026.
</p>
