using System.ComponentModel.DataAnnotations;

namespace Domic.Core.Domain.Enumerations;

public enum GradeType :byte
{
    [Display(Name = "School")]
    School	 = 0,
    
    [Display(Name = "Seminary")]
    Seminary = 1,
    
    [Display(Name = "University")]
    University = 2
}