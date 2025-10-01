from abc import ABC, abstractmethod
from typing import List
import numpy as np
from ..schema import Line

class ITxtExtractor(ABC):
    """Interface for OCR engines that return token/line boxes from a grayscale L8 image."""

    @abstractmethod
    def run(self, gray_l8: np.ndarray) -> List[Line]:
        """Return a list of Line(text, conf, bbox) from a grayscale (H,W) numpy array."""
        ...
