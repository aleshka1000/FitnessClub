using BusinessLogicLayer.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly IEnrollmentService _enrollmentService;
    private readonly IWaitlistService _waitlistService;

    public EnrollmentsController(IEnrollmentService enrollmentService, IWaitlistService waitlistService)
    {
        _enrollmentService = enrollmentService;
        _waitlistService = waitlistService;
    }
}
