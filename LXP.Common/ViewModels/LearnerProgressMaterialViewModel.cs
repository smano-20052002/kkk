﻿namespace LXP.Common.ViewModels
{
    public class LearnerProgressMaterialViewModel
    {
        public Guid MaterialId { get; set; }
        public string Name { get; set; } = null!;
        public string FilePath { get; set; } = null!;
        public TimeOnly Duration { get; set; }
        public bool IsActive { get; set; }
        public bool IsAvailable { get; set; }
    }
}
