﻿namespace Domic.Core.Domain.Contracts.Interfaces;

public interface IDateTime
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="dateTime"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public string ToPersianShortDate(DateTime dateTime) => throw new NotImplementedException();
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="dateTime"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public string ToPersianShortDateTime(DateTime dateTime) => throw new NotImplementedException();
    public string ToPersianShortDateOnly(DateOnly dateOnly) => throw new NotImplementedException();
    public DateTime Now() => throw new NotImplementedException();
}