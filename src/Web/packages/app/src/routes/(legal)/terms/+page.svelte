<script lang="ts">
  import { resolve } from "$app/paths";
</script>

<svelte:head>
  <title>Terms of Service - Nocturne</title>
</svelte:head>

<h1>Terms of Service</h1>

<p class="lead">
  Welcome to Nocturne. These terms explain your rights and responsibilities when using this open-source diabetes data management platform.
</p>

<h2>1. Introduction</h2>

<p>
  Nocturne is an open-source, self-hosted platform for managing your diabetes data with full Nightscout API compatibility. It's built by the diabetes community, for the diabetes community.
</p>

<p>
  Because Nocturne is self-hosted, you have full control over your installation, your data, and how you use the software. These terms apply to the Nocturne software itself, not to any specific hosted instance.
</p>

<h2>2. No Warranty - Provided "AS IS"</h2>

<p>
  Nocturne is provided <strong>"AS IS"</strong> without any warranties or guarantees of any kind, either express or implied. This means:
</p>

<ul>
  <li><strong>No medical advice:</strong> Nocturne is not a medical device and does not provide medical advice. Always consult with healthcare professionals for medical decisions.</li>
  <li><strong>No reliability guarantee:</strong> We don't guarantee the software will work perfectly, be error-free, or be available at all times.</li>
  <li><strong>No fitness guarantee:</strong> We don't warrant that Nocturne is suitable for any particular purpose or meets your specific needs.</li>
  <li><strong>Use at your own risk:</strong> You assume all risk when using Nocturne. We are not liable for any harm, data loss, or issues that may arise.</li>
</ul>

<p>
  This is standard for open-source software. You're free to inspect the code, modify it, and use it as you see fit, but we can't guarantee outcomes.
</p>

<h2>3. Third-Party Data Connectors</h2>

<p>
  Nocturne integrates with various third-party diabetes data services, including:
</p>

<ul>
  <li>Dexcom</li>
  <li>FreeStyle Libre</li>
  <li>Glooko</li>
  <li>Tidepool</li>
  <li>Nightscout</li>
  <li>And others</li>
</ul>

<p>
  <strong>Important disclaimers about third-party connectors:</strong>
</p>

<ul>
  <li><strong>We don't control these services:</strong> Third-party services have their own terms of service, privacy policies, and data practices. You are responsible for reviewing and agreeing to their terms.</li>
  <li><strong>Data accuracy:</strong> The accuracy of your data in Nocturne depends on the accuracy of data provided by these third-party sources. We cannot guarantee the accuracy of third-party data.</li>
  <li><strong>Service availability:</strong> Third-party services may change their APIs, terms, or shut down at any time. We are not responsible for service interruptions or changes to third-party integrations.</li>
  <li><strong>Your agreements:</strong> By connecting a third-party service to Nocturne, you agree that you have the right to access that data and are in compliance with that service's terms.</li>
  <li><strong>No liability:</strong> We are not liable for any issues, data loss, or problems arising from third-party services or connectors.</li>
</ul>

<h2>4. Your Data and Privacy</h2>

<p>
  Nocturne is designed with privacy as a core principle:
</p>

<ul>
  <li><strong>Self-hosted:</strong> Your data lives in your own database on your own server. We don't have access to it.</li>
  <li><strong>No data collection:</strong> The Nocturne software does not send data to us without your explicit consent.</li>
  <li><strong>You're in control:</strong> You decide where to host Nocturne, who has access, and how your data is used.</li>
</ul>

<p>
  For more details, see our <a href={resolve("/privacy")}>Privacy Policy</a>.
</p>

<h2>5. Acceptable Use</h2>

<p>
  When using Nocturne, you agree to:
</p>

<ul>
  <li>Use the software for lawful purposes only</li>
  <li>Not use it to harm others or violate their rights</li>
  <li>Not abuse the software or use it in ways that could damage the project or community</li>
  <li>Comply with all applicable laws and regulations</li>
</ul>

<p>
  If you're hosting Nocturne for others (family, friends, community), you're responsible for ensuring acceptable use by those users.
</p>

<h2>6. Changes to These Terms</h2>

<p>
  We may update these terms from time to time to reflect changes in the software, legal requirements, or best practices. When we do:
</p>

<ul>
  <li>The updated terms will be posted at this URL</li>
  <li>The "Last updated" date at the bottom of the page will change</li>
  <li>Continued use of Nocturne after changes constitutes acceptance of the new terms</li>
</ul>

<p>
  For significant changes, we'll announce them in the project's GitHub repository.
</p>

<h2>7. Open Source License</h2>

<p>
  Nocturne is released under the <a href="https://www.gnu.org/licenses/agpl-3.0.html" target="_blank" rel="noopener noreferrer">GNU Affero General Public License v3.0 (AGPL-3.0)</a>. This means you're free to:
</p>

<ul>
  <li>Use the software for any purpose</li>
  <li>Study and modify the source code</li>
  <li>Distribute copies, provided derivative works are also licensed under AGPL-3.0</li>
  <li>Run the software as a network service, provided users can access the source code</li>
</ul>

<p>
  Commercial licensing is available for organizations that need to use Nocturne without AGPL obligations. Contact the maintainers for details.
</p>

<p>
  The AGPL-3.0 provides the software "as is" without warranty. See the LICENSE file in the project repository for full terms.
</p>

<h2>8. Contact</h2>

<p>
  Nocturne is an open-source project. For questions, issues, or contributions:
</p>

<ul>
  <li>GitHub: <a href="https://github.com/nightscout/nocturne" target="_blank" rel="noopener noreferrer">github.com/nightscout/nocturne</a></li>
  <li>Issues: Use GitHub Issues for bug reports and feature requests</li>
  <li>Community: Join discussions in GitHub Discussions</li>
</ul>

<hr />

<p class="text-sm text-muted-foreground">
  These terms are effective as of February 21, 2026.
</p>
