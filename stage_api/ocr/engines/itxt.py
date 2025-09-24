from abc import ABC, abstractmethod
from typing import List
import numpy as np
from ..schema import Line

class ITxtExtractor(ABC):
    @abstractmethod
    def run(self, gray_l8: np.ndarray) -> List[Line]:
        ...
