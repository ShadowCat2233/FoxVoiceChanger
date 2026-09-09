use foxvoice_contracts::{GuardDecision, GuardLevel, PerformanceSample};

const OVERLOAD_STREAK: u8 = 3;
const RECOVERY_STREAK: u8 = 20;

#[derive(Debug, Default)]
pub struct GameGuard {
    level: GuardLevel,
    overload_streak: u8,
    recovery_streak: u8,
    last_underruns: u32,
}

impl GameGuard {
    pub fn level(&self) -> GuardLevel {
        self.level
    }

    pub fn observe(&mut self, sample: PerformanceSample) -> GuardDecision {
        let ratio = if sample.chunk_budget_ms > 0.0 {
            sample.inference_ms / sample.chunk_budget_ms
        } else {
            f32::INFINITY
        };
        let new_underrun = sample.underruns > self.last_underruns;
        self.last_underruns = sample.underruns;

        if ratio >= 1.0 || new_underrun || sample.gpu_load_percent >= 98.0 {
            self.overload_streak = self.overload_streak.saturating_add(1);
            self.recovery_streak = 0;
        } else if ratio <= 0.55 && sample.gpu_load_percent <= 85.0 {
            self.recovery_streak = self.recovery_streak.saturating_add(1);
            self.overload_streak = 0;
        } else {
            self.overload_streak = 0;
            self.recovery_streak = 0;
        }

        let mut reason = "实时预算稳定".to_string();
        if self.overload_streak >= OVERLOAD_STREAK {
            self.level = next_level(self.level);
            self.overload_streak = 0;
            reason = match self.level {
                GuardLevel::ReducedVisuals => "连续超预算：降低界面刷新率",
                GuardLevel::ReducedEffects => "持续超预算：关闭非必要后处理",
                GuardLevel::ExpandedBuffer => "持续超预算：扩大音频缓冲",
                GuardLevel::LightweightModel => "持续超预算：请求轻量模型",
                GuardLevel::SafeBypass => "无法满足实时预算：进入安全旁路",
                GuardLevel::Normal => "实时预算稳定",
            }
            .into();
        } else if self.recovery_streak >= RECOVERY_STREAK {
            self.level = previous_level(self.level);
            self.recovery_streak = 0;
            reason = "负载持续恢复：撤销一级降级".into();
        }

        GuardDecision {
            level: self.level,
            budget_ratio: ratio,
            reason,
        }
    }
}

fn next_level(level: GuardLevel) -> GuardLevel {
    match level {
        GuardLevel::Normal => GuardLevel::ReducedVisuals,
        GuardLevel::ReducedVisuals => GuardLevel::ReducedEffects,
        GuardLevel::ReducedEffects => GuardLevel::ExpandedBuffer,
        GuardLevel::ExpandedBuffer => GuardLevel::LightweightModel,
        GuardLevel::LightweightModel | GuardLevel::SafeBypass => GuardLevel::SafeBypass,
    }
}

fn previous_level(level: GuardLevel) -> GuardLevel {
    match level {
        GuardLevel::Normal | GuardLevel::ReducedVisuals => GuardLevel::Normal,
        GuardLevel::ReducedEffects => GuardLevel::ReducedVisuals,
        GuardLevel::ExpandedBuffer => GuardLevel::ReducedEffects,
        GuardLevel::LightweightModel => GuardLevel::ExpandedBuffer,
        GuardLevel::SafeBypass => GuardLevel::LightweightModel,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn overloaded(underruns: u32) -> PerformanceSample {
        PerformanceSample {
            inference_ms: 21.0,
            chunk_budget_ms: 20.0,
            gpu_load_percent: 99.0,
            underruns,
        }
    }

    #[test]
    fn requires_consecutive_overload_before_degrading() {
        let mut guard = GameGuard::default();
        guard.observe(overloaded(0));
        guard.observe(overloaded(0));
        assert_eq!(guard.level(), GuardLevel::Normal);
        guard.observe(overloaded(0));
        assert_eq!(guard.level(), GuardLevel::ReducedVisuals);
    }

    #[test]
    fn repeated_pressure_reaches_safe_bypass() {
        let mut guard = GameGuard::default();
        for _ in 0..15 {
            guard.observe(overloaded(0));
        }
        assert_eq!(guard.level(), GuardLevel::SafeBypass);
    }
}
