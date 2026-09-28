// Posts the coverage report as one sticky comment on a pull request, updating it in place.
// Called through actions/github-script: by tests.yml's coverage job for a same-repository pull
// request, and by coverage-comment.yml for a fork's, whose Tests run has no write token.

const MARKER = "<!-- nocturne-coverage -->";
// GitHub rejects comments over 65,536 characters.
const MAX_LENGTH = 60000;

module.exports = async function postCoverageComment({ github, context, prNumber, body }) {
  const { owner, repo } = context.repo;
  if (!body.startsWith(MARKER)) body = `${MARKER}\n${body}`;
  if (body.length > MAX_LENGTH) body = body.slice(0, MAX_LENGTH) + "\n\n_Report truncated._\n";

  const comments = await github.paginate(github.rest.issues.listComments, {
    owner,
    repo,
    issue_number: prNumber,
    per_page: 100,
  });
  const existing = comments.find((c) => c.user?.type === "Bot" && c.body?.startsWith(MARKER));

  if (existing) {
    await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body });
  } else {
    await github.rest.issues.createComment({ owner, repo, issue_number: prNumber, body });
  }
};
