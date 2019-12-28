using System;

namespace RH.Attributes
{
    /// <summary>
    /// Any property with this attribute will mark the child object to not be deleted during a trash action
    /// </summary>
    public class NoTrashAttribute : Attribute
    {
    }
}