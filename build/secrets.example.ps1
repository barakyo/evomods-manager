# Copy this to build\secrets.ps1 and fill in the token. That copy is gitignored; this one is not,
# which is why this one must never hold a real value.
#
# Fine-grained personal access token, scoped to this repository, one permission:
#   Contents -> Read and write
#
# That is everything `vpk upload github` needs in order to create a release and attach files to it.

$env:GITHUB_TOKEN = 'github_pat_...'
