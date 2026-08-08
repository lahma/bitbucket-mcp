namespace Bitbucket.Mcp.Http;

/// <summary>
/// Inclusive <c>fields=</c> lists, one per endpoint. Partial responses are the single biggest
/// lever on how much of the model's context a Bitbucket call costs: a pull request object carries
/// a dozen <c>links</c> sub-objects, rendered HTML bodies and repository echoes that no tool here
/// ever reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule: every paginated field set must contain <c>next</c>.</b> An inclusive <c>fields=</c>
/// list returns <em>only</em> what it names, and <c>next</c> is no exception — leave it out and
/// Bitbucket happily returns page one with no <c>next</c> link, the client concludes there is
/// nothing more, and pagination is silently truncated to a single page. This is invisible in
/// testing against small repositories and wrong in production, so a test enforces it: any constant
/// in this class whose value mentions <c>values.</c> is paginated and must also mention
/// <c>next</c>.
/// </para>
/// <para>
/// Each list must stay in step with the DTO it fills. A field that is not requested deserialises
/// as <see langword="null"/>, which is indistinguishable from "not set" — so adding a property to
/// a DTO without adding it here produces a silent null, not an error.
/// </para>
/// </remarks>
internal static class FieldSets
{
    /// <summary>
    /// <c>GET /pullrequests</c> — fills <c>PullRequestSummaryDto</c>. Deliberately without
    /// descriptions, participants or reviewers: a list of fifty pull requests is a summary, and
    /// anything more is a per-pull-request fetch.
    /// </summary>
    internal const string PullRequestList =
        "next,size," +
        "values.id,values.title,values.state,values.draft," +
        "values.created_on,values.updated_on,values.comment_count,values.task_count," +
        "values.close_source_branch," +
        "values.author.display_name,values.author.uuid,values.author.nickname," +
        "values.source.branch.name,values.source.commit.hash,values.source.repository.full_name," +
        "values.destination.branch.name,values.destination.commit.hash,values.destination.repository.full_name";

    /// <summary>
    /// <c>GET|POST|PUT /pullrequests[/{id}]</c> and the approve/decline/merge responses — fills
    /// <c>PullRequestDto</c>. Not paginated, so no <c>next</c>.
    /// </summary>
    internal const string PullRequestDetail =
        "id,title,state,draft,created_on,updated_on,comment_count,task_count,close_source_branch," +
        "description,reason," +
        "author.display_name,author.uuid,author.nickname," +
        "closed_by.display_name,closed_by.uuid,closed_by.nickname," +
        "source.branch.name,source.commit.hash,source.repository.full_name," +
        "destination.branch.name,destination.commit.hash,destination.repository.full_name," +
        "merge_commit.hash," +
        "reviewers.display_name,reviewers.uuid,reviewers.nickname," +
        "participants.role,participants.approved,participants.state," +
        "participants.user.display_name,participants.user.uuid,participants.user.nickname";

    /// <summary>
    /// <c>GET /pullrequests/{id}/comments</c> — fills <c>CommentDto</c>. <c>deleted</c> is
    /// requested because deleted comments are still returned and have to be filtered out.
    /// </summary>
    internal const string Comments =
        "next,size," +
        "values.id,values.created_on,values.updated_on,values.deleted," +
        "values.content.raw," +
        "values.user.display_name,values.user.uuid,values.user.nickname," +
        "values.parent.id," +
        "values.inline.path,values.inline.from,values.inline.to," +
        "values.inline.start_from,values.inline.start_to," +
        "values.resolution.created_on," +
        "values.resolution.user.display_name,values.resolution.user.uuid,values.resolution.user.nickname";

    /// <summary>
    /// <c>POST /pullrequests/{id}/comments</c> — the single comment that was just created, filling
    /// <c>CommentDto</c>. Not paginated, so no <c>next</c>.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="Comments"/> without the <c>values.</c> prefix; the two describe
    /// one DTO and have to be kept in step.
    /// </remarks>
    internal const string Comment =
        "id,created_on,updated_on,deleted," +
        "content.raw," +
        "user.display_name,user.uuid,user.nickname," +
        "parent.id," +
        "inline.path,inline.from,inline.to,inline.start_from,inline.start_to," +
        "resolution.created_on," +
        "resolution.user.display_name,resolution.user.uuid,resolution.user.nickname";

    /// <summary>
    /// <c>POST /pullrequests/{id}/approve</c> and <c>…/request-changes</c> — fills
    /// <c>ParticipantDto</c>. These endpoints answer with the caller's own participant entry rather
    /// than the pull request. Not paginated, so no <c>next</c>.
    /// </summary>
    internal const string Participant =
        "role,approved,state," +
        "user.display_name,user.uuid,user.nickname";

    /// <summary>
    /// <c>GET /pullrequests/{id}/diffstat</c> — fills <c>DiffStatEntryDto</c>. This is the entry
    /// point of every diff workflow, so it is kept as small as it can usefully be.
    /// </summary>
    internal const string DiffStat =
        "next,size," +
        "values.status,values.lines_added,values.lines_removed," +
        "values.old.path,values.new.path";
}
